using System.Security.Cryptography;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using BJK.Modules.Media.Infrastructure.Processing.Safety;
using BJK.Modules.Media.Infrastructure.Processing.Sensitive;
using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>What the worker should do with the queue message after a job.</summary>
public enum ProcessingOutcome
{
    /// <summary>Published, or nothing left to do (deleted, already processed): acknowledge.</summary>
    Completed,

    /// <summary>Can never succeed, or out of attempts; the media is marked Failed: acknowledge.</summary>
    Failed,

    /// <summary>Something temporary went wrong: put the job back for another attempt.</summary>
    Retry,

    /// <summary>Still failing on the last attempt; the media is marked Failed and the job goes to the failed table.</summary>
    GaveUp,
}

/// <summary>
/// Turns one uploaded original into the published files: downloads it, checks it, blurs faces and number plates
/// (<see cref="IMediaRedactor"/>), converts it with ffmpeg (stripping metadata such as GPS, and the soundtrack), then
/// either publishes the results and marks the media Ready, or, if detection failed or wasn't sure, keeps them in the
/// private bucket and marks it NeedsReview for a moderator. Thumbnails are always made from the blurred files. Media
/// marked sensitive (in a post tagged sensitive) is Ready with its files in the sensitive bucket instead, and only a
/// heavily blurred preview in the public bucket; <see cref="MediaJobStep.Visibility"/> jobs move the files when the
/// mark changes later.
/// </summary>
/// <remarks>
/// Videos longer than <see cref="MediaProcessingOptions.SegmentSeconds"/> are split: the first job plans the segments
/// (<see cref="IVideoJobStore"/>) and queues one <see cref="MediaJobStep.Segment"/> job per segment, which any worker
/// replica can pick up; the job that finishes the last segment queues <see cref="MediaJobStep.Assemble"/>, which joins
/// them, makes the thumbnail and publishes. Segment and assemble jobs never touch the media row until the end, so
/// parallel workers don't conflict, and every step is safe to repeat.
/// </remarks>
internal sealed partial class MediaProcessor(
    IMediaRepository media,
    IVideoJobStore videoJobs,
    IMediaJobQueue queue,
    IMediaStorage storage,
    IMediaTranscoder transcoder,
    IMediaRedactor redactor,
    IOptions<MediaOptions> mediaOptions,
    IOptions<MediaProcessingOptions> processingOptions,
    IOptions<MediaRedactionOptions> redactionOptions,
    TimeProvider clock,
    ILogger<MediaProcessor> logger,
    IMediaCachePurger? cachePurger = null,
    IOptions<SensitiveDetectionOptions>? detectionOptions = null,
    IKnownImageList? knownImages = null,
    IKnownImageRepository? knownImageRepository = null)
{
    /// <summary>Logged once for every upload held for a moderator, with its id and the reasons.</summary>
    private static readonly EventId NeedsReviewEvent = new(4802, "MediaNeedsReview");

    private const string UnreadableReason = "We couldn't read this file. Try exporting it from your dashcam app again.";
    private const string GaveUpReason = "Processing failed. Try uploading the file again.";

    private readonly MediaOptions _limits = mediaOptions.Value;
    private readonly MediaProcessingOptions _options = processingOptions.Value;
    private readonly MediaRedactionOptions _redaction = redactionOptions.Value;
    private readonly SensitiveDetectionOptions _detection = detectionOptions?.Value ?? new SensitiveDetectionOptions();
    private readonly IMediaCachePurger _purger = cachePurger ?? new LoggingMediaCachePurger(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<LoggingMediaCachePurger>.Instance);

    /// <param name="job">The queue message.</param>
    /// <param name="isLastAttempt">If true, a temporary failure marks the media Failed instead of retrying.</param>
    /// <param name="cancellationToken">Stops the job when the worker shuts down; the job is then retried.</param>
    /// <param name="isRedelivery">True if the job has been delivered before (a worker stopped, failed or crashed on it).</param>
    public Task<ProcessingOutcome> ProcessAsync(
        MediaProcessingJob job,
        bool isLastAttempt,
        CancellationToken cancellationToken,
        bool isRedelivery = false) =>
        job.Step switch
        {
            MediaJobStep.Segment => ProcessSegmentAsync(job, isLastAttempt, isRedelivery, cancellationToken),
            MediaJobStep.Assemble => AssembleAsync(job, isLastAttempt, cancellationToken),
            MediaJobStep.Visibility => ReconcileVisibilityAsync(job, cancellationToken),
            _ => ProcessMediaAsync(job, isLastAttempt, cancellationToken),
        };

    /// <summary>
    /// The queue gave up on a job (its attempts ran out without a result, e.g. the worker crashed on each): marks the
    /// media Failed if it's still processing this revision, so the uploader is told and nothing stays at Processing.
    /// </summary>
    /// <param name="job">The job given up on.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task GiveUpAsync(MediaProcessingJob job, CancellationToken cancellationToken)
    {
        if (job.Step == MediaJobStep.Visibility)
        {
            // Moving files never fails the media: the job stays in the failed table to be replayed.
            logger.LogError("Gave up moving the files of media {MediaId} for its sensitive tag; replay it from the failed table.", job.MediaId);
            return;
        }

        var item = await media.GetByIdAsync(job.MediaId, cancellationToken);
        if (item is null || item.Status != MediaStatus.Processing || (job.Step != MediaJobStep.Process && item.Revision != job.Revision))
        {
            return;
        }

        logger.LogError("Giving up on media {MediaId} ({Step}): every attempt failed or never finished.", job.MediaId, job.Step);
        await FailAsync(item, GaveUpReason, cancellationToken);
    }

    private async Task<ProcessingOutcome> ProcessMediaAsync(MediaProcessingJob job, bool isLastAttempt, CancellationToken cancellationToken)
    {
        var item = await media.GetByIdAsync(job.MediaId, cancellationToken);
        if (item is null || item.Status != MediaStatus.Processing)
        {
            // Deleted, or already handled by an earlier delivery of the same job.
            logger.LogInformation("Skipping media {MediaId}: it is {Status}.", job.MediaId, item?.Status.ToString() ?? "gone");
            return ProcessingOutcome.Completed;
        }

        item.RecordProcessingAttempt(clock.GetUtcNow());
        await media.SaveChangesAsync(cancellationToken);

        return await InFolderAsync(
            item,
            "",
            isLastAttempt,
            folder => ProcessFilesAsync(item, job, folder, cancellationToken),
            cancellationToken);
    }

    private async Task<ProcessingOutcome> ProcessFilesAsync(MediaItem item, MediaProcessingJob job, string folder, CancellationToken cancellationToken)
    {
        var original = Path.Combine(folder, SourceFileName(item));
        var sha256 = await DownloadOriginalAsync(item, original, cancellationToken);

        // Exact copies of known images are stopped before any processing.
        if (MatchFile(item, sha256, knownImages is null ? null : await Md5Async(item, original, cancellationToken)) is { } fileMatch)
        {
            return await BlockAsync(item, fileMatch, sha256, cancellationToken);
        }

        var probe = await transcoder.ProbeAsync(original, cancellationToken);
        if (!probe.HasVideo)
        {
            return await FailAsync(item, "This file has no picture. Upload a video or photo.", cancellationToken);
        }

        var playback = Path.Combine(folder, item.MediaType == MediaType.Video ? MediaKeys.VideoPlaybackFile : MediaKeys.ImageFile);
        var thumbnail = Path.Combine(folder, item.MediaType == MediaType.Video ? MediaKeys.VideoThumbnailFile : MediaKeys.ImageThumbnailFile);
        var manual = item.ManualRedactions;
        string? feed = null;
        RedactionResult redaction;

        if (item.MediaType == MediaType.Video)
        {
            if (probe.DurationSeconds is { } seconds && _limits.IsTooLong(seconds))
            {
                return await FailAsync(item, _limits.TooLongMessage(seconds), cancellationToken);
            }

            var plan = VideoSegmentPlan.For(
                (double)(probe.DurationSeconds ?? 0),
                MediaRedactor.OutputFrameRate(probe.FrameRate),
                probe.StartSeconds ?? 0,
                _options.SegmentSeconds,
                _options.SegmentOverlapSeconds);
            if (plan.IsSplit)
            {
                return await SplitAsync(item, job, plan, probe, sha256, cancellationToken);
            }

            redaction = await redactor.RedactVideoAsync(original, probe, manual, playback, cancellationToken);
            await transcoder.CreateVideoThumbnailAsync(playback, thumbnail, cancellationToken);
        }
        else
        {
            var redacted = Path.Combine(folder, "redacted.ppm");
            redaction = await redactor.RedactImageAsync(original, manual, redacted, cancellationToken);
            await transcoder.ConvertImageAsync(redacted, playback, thumbnail, cancellationToken);
            feed = Path.Combine(folder, MediaKeys.FeedImageFile);
            await transcoder.CreateFeedImageAsync(redacted, feed, cancellationToken);
        }

        if (await MatchFromReasonsAsync(redaction.ReviewReasons, cancellationToken) is { } frameMatch)
        {
            return await BlockAsync(item, frameMatch, sha256, cancellationToken);
        }

        var duration = item.MediaType == MediaType.Video ? probe.DurationSeconds : null;
        item.RecordRedaction(redaction.FacesBlurred, redaction.PlatesBlurred);
        item.RecordContentChecks(redaction.NudityScore, redaction.PerceptualHashes);
        item.RecordClipSummary(redaction.ClipSummary);
        await ApplySensitiveScoreAsync(item, redaction.SensitiveScore, cancellationToken);
        var outcome = await PublishAsync(
            item,
            playback,
            thumbnail,
            ReviewReasons(redaction.ReviewReasons, manual.Count),
            new ProcessedDetails(duration, probe.Width, probe.Height, sha256),
            beforeSave: null,
            cancellationToken,
            feed);

        logger.LogInformation(
            "Media {MediaId} is {Status}: {Faces} faces and {Plates} plates blurred in {Elapsed} ({Frames} frames); {Classified} frames checked for known images, graphic content and nudity in {ClassificationTime}.",
            item.Id,
            item.Status,
            redaction.FacesBlurred,
            redaction.PlatesBlurred,
            redaction.Elapsed,
            redaction.Frames,
            redaction.FramesClassified,
            redaction.ClassificationTime);
        return outcome;
    }

    /// <summary>
    /// Records the graphic content score and, at or above <see cref="SensitiveDetectionOptions.Threshold"/>, marks the
    /// media sensitive automatically. Called before publishing, which reads the mark, so flagged media is never public.
    /// </summary>
    private async Task ApplySensitiveScoreAsync(MediaItem item, double? score, CancellationToken cancellationToken)
    {
        item.RecordSensitiveScore(score);
        if (score is not { } value || value < _detection.Threshold)
        {
            return;
        }

        var marked = await media.SetSensitiveAsync(
            item.Id, isSensitive: true, clock.GetUtcNow(), cancellationToken, SensitiveMarkSource.Automatic, Math.Round(value, 4));
        logger.LogInformation(
            "Media {MediaId} scored {Score:0.000} for graphic content (threshold {Threshold}); {Action}.",
            item.Id,
            value,
            _detection.Threshold,
            marked ? "marked sensitive automatically" : "it was already marked sensitive");
    }

    /// <summary>Why the media must be reviewed: the redactor's reasons plus any manual regions, without repeats.</summary>
    private static List<string> ReviewReasons(IEnumerable<string> reasons, int manualRegions)
    {
        var all = WithoutKnownImage(reasons).Distinct().ToList();
        if (manualRegions > 0)
        {
            all.Add($"A moderator added {manualRegions} blur region(s); check the result.");
        }

        return all;
    }

    /// <summary>
    /// Uploads the blurred files and marks the media Ready (public bucket) or NeedsReview (private bucket, if there are
    /// <paramref name="reasons"/>), then saves. If the media changed meanwhile (the owner deleted it), the files just
    /// stored are removed, unless another delivery of the same job already finished it.
    /// </summary>
    private async Task<ProcessingOutcome> PublishAsync(
        MediaItem item,
        string playback,
        string thumbnail,
        IReadOnlyList<string> reasons,
        ProcessedDetails details,
        Action? beforeSave,
        CancellationToken cancellationToken,
        string? feed = null)
    {
        var (playbackType, thumbnailType) = MediaFiles.ContentTypes(item.MediaType);
        var revision = item.Revision;
        string playbackKey;
        string thumbnailKey;
        string? feedKey = null;
        string? previewKey = null;

        // Media in a post tagged sensitive is never published: real files to the sensitive bucket, a blurred preview to the public one.
        var sensitive = reasons.Count == 0 && await media.IsSensitiveAsync(item.Id, cancellationToken);
        if (reasons.Count > 0)
        {
            // Not public until a moderator approves: the blurred files wait in the private bucket.
            playbackKey = MediaKeys.ReviewPlayback(item.UserId, item.Id, item.MediaType, revision);
            thumbnailKey = MediaKeys.ReviewThumbnail(item.UserId, item.Id, item.MediaType, revision);
            await storage.UploadPrivateAsync(playbackKey, playback, playbackType, cancellationToken);
            await storage.UploadPrivateAsync(thumbnailKey, thumbnail, thumbnailType, cancellationToken);
            if (feed is not null)
            {
                feedKey = MediaKeys.ReviewFeed(item.UserId, item.Id, revision);
                await storage.UploadPrivateAsync(feedKey, feed, "image/webp", cancellationToken);
            }

            item.MarkNeedsReview(string.Join(" ", reasons), new FileInfo(playback).Length, details.DurationSeconds, details.Width, details.Height, details.Sha256, clock.GetUtcNow());

            // One line per held upload, so it's easy to find why (Kibana: event.name "MediaNeedsReview"). No personal data.
            logger.LogWarning(
                NeedsReviewEvent,
                "Media {MediaId} ({MediaType}) needs review: {ReviewReasons} Faces blurred {Faces}, plates blurred {Plates}, nudity score {NudityScore}, graphic score {SensitiveScore}.",
                item.Id,
                item.MediaType,
                string.Join(" ", reasons),
                item.FacesBlurred,
                item.PlatesBlurred,
                item.NudityScore,
                item.SensitiveScore);
        }
        else if (sensitive)
        {
            playbackKey = MediaKeys.Sensitive(MediaKeys.Playback(item.UserId, item.Id, item.MediaType, revision));
            thumbnailKey = MediaKeys.Sensitive(MediaKeys.Thumbnail(item.UserId, item.Id, item.MediaType, revision));
            await storage.UploadAsync(MediaBucket.Sensitive, playbackKey, playback, playbackType, cancellationToken);
            await storage.UploadAsync(MediaBucket.Sensitive, thumbnailKey, thumbnail, thumbnailType, cancellationToken);
            if (feed is not null)
            {
                feedKey = MediaKeys.Sensitive(MediaKeys.Feed(item.UserId, item.Id, revision));
                await storage.UploadAsync(MediaBucket.Sensitive, feedKey, feed, "image/webp", cancellationToken);
            }

            previewKey = MediaKeys.Preview(item.UserId, item.Id, revision);
            await UploadPreviewAsync(thumbnail, previewKey, cancellationToken);
            item.MarkReady(new FileInfo(playback).Length, details.DurationSeconds, details.Width, details.Height, details.Sha256, clock.GetUtcNow());
            item.Restrict(previewKey, clock.GetUtcNow());
        }
        else
        {
            playbackKey = MediaKeys.Playback(item.UserId, item.Id, item.MediaType, revision);
            thumbnailKey = MediaKeys.Thumbnail(item.UserId, item.Id, item.MediaType, revision);
            await storage.UploadPublicAsync(playbackKey, playback, playbackType, cancellationToken);
            await storage.UploadPublicAsync(thumbnailKey, thumbnail, thumbnailType, cancellationToken);
            if (feed is not null)
            {
                feedKey = MediaKeys.Feed(item.UserId, item.Id, revision);
                await storage.UploadPublicAsync(feedKey, feed, "image/webp", cancellationToken);
            }

            item.MarkReady(new FileInfo(playback).Length, details.DurationSeconds, details.Width, details.Height, details.Sha256, clock.GetUtcNow());
        }

        beforeSave?.Invoke();
        try
        {
            await media.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var saved = await media.GetSavedStateAsync(item.Id, CancellationToken.None);
            if (saved is { Status: MediaStatus.Ready or MediaStatus.NeedsReview } state && state.Revision == revision)
            {
                // A repeat delivery of the same job finished first and stored the same files under the same names.
                logger.LogInformation("Media {MediaId} was already finished by another delivery of its job.", item.Id);
                return ProcessingOutcome.Completed;
            }

            // The owner deleted it while we were working: remove what we just stored.
            logger.LogInformation("Media {MediaId} changed while processing; removing its processed files.", item.Id);
            var stored = new[] { playbackKey, thumbnailKey, feedKey }.OfType<string>().ToList();
            if (reasons.Count > 0)
            {
                foreach (var key in stored)
                {
                    await storage.DeleteOriginalAsync(key, CancellationToken.None);
                }
            }
            else if (sensitive)
            {
                await storage.DeleteAsync(MediaBucket.Sensitive, stored, CancellationToken.None);
                await storage.DeleteAsync(MediaBucket.Public, [previewKey!], CancellationToken.None);
            }
            else
            {
                await storage.DeletePublicAsync(stored, CancellationToken.None);
            }

            return ProcessingOutcome.Completed;
        }

        if (reasons.Count == 0)
        {
            // The post may have been tagged or untagged while we worked: if the mark changed, move the files now.
            await CatchUpVisibilityAsync(item, sensitive, cancellationToken);
        }

        return ProcessingOutcome.Completed;
    }

    /// <summary>Makes sensitive media's blurred preview from its (already blurred) thumbnail and publishes it.</summary>
    private async Task UploadPreviewAsync(string thumbnailPath, string previewKey, CancellationToken cancellationToken)
    {
        var preview = Path.Combine(Path.GetDirectoryName(thumbnailPath) ?? Path.GetTempPath(), MediaKeys.PreviewFile);
        await transcoder.CreateSensitivePreviewAsync(thumbnailPath, preview, cancellationToken);
        await storage.UploadAsync(MediaBucket.Public, previewKey, preview, "image/webp", cancellationToken);
    }

    /// <summary>
    /// Runs one job's file work in its own temporary folder, turning failures into the right outcome: an unreadable
    /// file fails at once, anything else is retried until the last attempt.
    /// </summary>
    private async Task<ProcessingOutcome> InFolderAsync(
        MediaItem item,
        string suffix,
        bool isLastAttempt,
        Func<string, Task<ProcessingOutcome>> work,
        CancellationToken cancellationToken,
        Func<Task>? onFailed = null)
    {
        var folder = Path.Combine(_options.WorkingDirectory ?? Path.GetTempPath(), "bjk-media", item.Id.ToString("N") + suffix);
        Directory.CreateDirectory(folder);
        try
        {
            return await work(folder);
        }
        catch (UnreadableMediaException ex)
        {
            logger.LogWarning(ex, "Media {MediaId} can't be processed.", item.Id);
            await TryAsync(onFailed);
            return await FailAsync(item, UnreadableReason, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (isLastAttempt)
        {
            logger.LogError(ex, "Processing media {MediaId} failed on its last attempt.", item.Id);
            await TryAsync(onFailed);
            await FailAsync(item, GaveUpReason, CancellationToken.None);
            return ProcessingOutcome.GaveUp;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Processing media {MediaId} failed; it will be retried.", item.Id);
            return ProcessingOutcome.Retry;
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private async Task TryAsync(Func<Task>? cleanUp)
    {
        if (cleanUp is null)
        {
            return;
        }

        try
        {
            await cleanUp();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cleaning up after a failed media job didn't work; the 90-day storage rule will remove the files.");
        }
    }

    /// <summary>
    /// Downloads the file to process to <paramref name="path"/>: the original, or, when a moderator's added blur regions
    /// are being applied, the copy of the processed file (<see cref="MediaItem.SourceKey"/>), since originals aren't kept
    /// once a post is published. Returns the original's SHA-256 (hex): computed while downloading it, or the one
    /// recorded at the first processing.
    /// </summary>
    private async Task<string> DownloadOriginalAsync(MediaItem item, string path, CancellationToken cancellationToken)
    {
        await using var file = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var hashing = new HashingStream(file, hash);
        await storage.DownloadOriginalAsync(item.SourceKey ?? item.OriginalKey, hashing, cancellationToken);
        var computed = Convert.ToHexStringLower(hash.GetHashAndReset());
        return item.SourceKey is not null && item.Sha256 is { } recorded ? recorded : computed;
    }

    /// <summary>The local file name for the file being processed, keeping its extension.</summary>
    private static string SourceFileName(MediaItem item) => "original" + Path.GetExtension(item.SourceKey ?? item.OriginalKey);

    private async Task<ProcessingOutcome> FailAsync(MediaItem item, string reason, CancellationToken cancellationToken)
    {
        item.MarkFailed(reason, clock.GetUtcNow());
        try
        {
            await media.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted meanwhile; nothing to record.
        }

        return ProcessingOutcome.Failed;
    }

    private void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't remove the temporary folder {Folder}.", folder);
        }
    }

    /// <summary>What processing found out about the original, recorded when the media is finished.</summary>
    private sealed record ProcessedDetails(decimal? DurationSeconds, int? Width, int? Height, string? Sha256);

    /// <summary>Hashes everything written through it, so the original is hashed while it downloads.</summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
