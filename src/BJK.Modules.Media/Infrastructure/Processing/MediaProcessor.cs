using System.Security.Cryptography;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
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
}

/// <summary>
/// Turns one uploaded original into the published files: downloads it, checks it, blurs faces and number plates
/// (<see cref="IMediaRedactor"/>), converts it with ffmpeg (stripping metadata such as GPS, and the soundtrack), then
/// either publishes the results and marks the media Ready, or, if detection failed or wasn't sure, keeps them in the
/// private bucket and marks it NeedsReview for a moderator. Thumbnails are always made from the blurred files.
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
    ILogger<MediaProcessor> logger)
{
    private const string UnreadableReason = "We couldn't read this file. Try exporting it from your dashcam app again.";
    private const string GaveUpReason = "Processing failed. Try uploading the file again.";

    private readonly MediaOptions _limits = mediaOptions.Value;
    private readonly MediaProcessingOptions _options = processingOptions.Value;
    private readonly MediaRedactionOptions _redaction = redactionOptions.Value;

    /// <param name="job">The queue message.</param>
    /// <param name="isLastAttempt">If true, a temporary failure marks the media Failed instead of retrying.</param>
    /// <param name="cancellationToken">Stops the job when the worker shuts down; the job is then retried.</param>
    /// <param name="isRedelivery">True if RabbitMQ has delivered this message before (a worker stopped or failed on it).</param>
    public Task<ProcessingOutcome> ProcessAsync(
        MediaProcessingJob job,
        bool isLastAttempt,
        CancellationToken cancellationToken,
        bool isRedelivery = false) =>
        job.Step switch
        {
            MediaJobStep.Segment => ProcessSegmentAsync(job, isLastAttempt, isRedelivery, cancellationToken),
            MediaJobStep.Assemble => AssembleAsync(job, isLastAttempt, cancellationToken),
            _ => ProcessMediaAsync(job, isLastAttempt, cancellationToken),
        };

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
        var original = Path.Combine(folder, "original" + Path.GetExtension(item.OriginalKey));
        var sha256 = await DownloadOriginalAsync(item, original, cancellationToken);

        var probe = await transcoder.ProbeAsync(original, cancellationToken);
        if (!probe.HasVideo)
        {
            return await FailAsync(item, "This file has no picture. Upload a video or photo.", cancellationToken);
        }

        var playback = Path.Combine(folder, item.MediaType == MediaType.Video ? MediaKeys.VideoPlaybackFile : MediaKeys.ImageFile);
        var thumbnail = Path.Combine(folder, item.MediaType == MediaType.Video ? MediaKeys.VideoThumbnailFile : MediaKeys.ImageThumbnailFile);
        var manual = item.ManualRedactions;
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
        }

        var duration = item.MediaType == MediaType.Video ? probe.DurationSeconds : null;
        item.RecordRedaction(redaction.FacesBlurred, redaction.PlatesBlurred);
        var outcome = await PublishAsync(
            item,
            playback,
            thumbnail,
            ReviewReasons(redaction.ReviewReasons, manual.Count),
            new ProcessedDetails(duration, probe.Width, probe.Height, sha256),
            beforeSave: null,
            cancellationToken);

        logger.LogInformation(
            "Media {MediaId} is {Status}: {Faces} faces and {Plates} plates blurred in {Elapsed} ({Frames} frames).",
            item.Id,
            item.Status,
            redaction.FacesBlurred,
            redaction.PlatesBlurred,
            redaction.Elapsed,
            redaction.Frames);
        return outcome;
    }

    /// <summary>Why the media must be reviewed: the redactor's reasons plus any manual regions, without repeats.</summary>
    private static List<string> ReviewReasons(IEnumerable<string> reasons, int manualRegions)
    {
        var all = reasons.Distinct().ToList();
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
        CancellationToken cancellationToken)
    {
        var (playbackType, thumbnailType) = item.MediaType == MediaType.Video
            ? ("video/mp4", "image/jpeg")
            : ("image/webp", "image/webp");
        var revision = item.Revision;
        string playbackKey;
        string thumbnailKey;
        if (reasons.Count > 0)
        {
            // Not public until a moderator approves: the blurred files wait in the private bucket.
            playbackKey = MediaKeys.ReviewPlayback(item.UserId, item.Id, item.MediaType, revision);
            thumbnailKey = MediaKeys.ReviewThumbnail(item.UserId, item.Id, item.MediaType, revision);
            await storage.UploadPrivateAsync(playbackKey, playback, playbackType, cancellationToken);
            await storage.UploadPrivateAsync(thumbnailKey, thumbnail, thumbnailType, cancellationToken);
            item.MarkNeedsReview(string.Join(" ", reasons), new FileInfo(playback).Length, details.DurationSeconds, details.Width, details.Height, details.Sha256, clock.GetUtcNow());
        }
        else
        {
            playbackKey = MediaKeys.Playback(item.UserId, item.Id, item.MediaType, revision);
            thumbnailKey = MediaKeys.Thumbnail(item.UserId, item.Id, item.MediaType, revision);
            await storage.UploadPublicAsync(playbackKey, playback, playbackType, cancellationToken);
            await storage.UploadPublicAsync(thumbnailKey, thumbnail, thumbnailType, cancellationToken);
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
            if (reasons.Count > 0)
            {
                await storage.DeleteOriginalAsync(playbackKey, CancellationToken.None);
                await storage.DeleteOriginalAsync(thumbnailKey, CancellationToken.None);
            }
            else
            {
                await storage.DeletePublicAsync([playbackKey, thumbnailKey], CancellationToken.None);
            }
        }

        return ProcessingOutcome.Completed;
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
            return await FailAsync(item, GaveUpReason, CancellationToken.None);
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

    /// <summary>Downloads the original to <paramref name="path"/>, returning its SHA-256 (hex).</summary>
    private async Task<string> DownloadOriginalAsync(MediaItem item, string path, CancellationToken cancellationToken)
    {
        await using var file = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var hashing = new HashingStream(file, hash);
        await storage.DownloadOriginalAsync(item.OriginalKey, hashing, cancellationToken);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

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
