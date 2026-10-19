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
internal sealed class MediaProcessor(
    IMediaRepository media,
    IMediaStorage storage,
    IMediaTranscoder transcoder,
    IMediaRedactor redactor,
    IOptions<MediaOptions> mediaOptions,
    IOptions<MediaProcessingOptions> processingOptions,
    TimeProvider clock,
    ILogger<MediaProcessor> logger)
{
    private readonly MediaOptions _limits = mediaOptions.Value;
    private readonly MediaProcessingOptions _options = processingOptions.Value;

    /// <param name="job">The queue message.</param>
    /// <param name="isLastAttempt">If true, a temporary failure marks the media Failed instead of retrying.</param>
    /// <param name="cancellationToken">Stops the job when the worker shuts down; the job is then retried.</param>
    public async Task<ProcessingOutcome> ProcessAsync(MediaProcessingJob job, bool isLastAttempt, CancellationToken cancellationToken)
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

        var folder = Path.Combine(_options.WorkingDirectory ?? Path.GetTempPath(), "bjk-media", item.Id.ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            return await ProcessFilesAsync(item, folder, cancellationToken);
        }
        catch (UnreadableMediaException ex)
        {
            logger.LogWarning(ex, "Media {MediaId} can't be processed.", item.Id);
            return await FailAsync(item, "We couldn't read this file. Try exporting it from your dashcam app again.", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (isLastAttempt)
        {
            logger.LogError(ex, "Processing media {MediaId} failed on its last attempt.", item.Id);
            return await FailAsync(item, "Processing failed. Try uploading the file again.", CancellationToken.None);
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

    private async Task<ProcessingOutcome> ProcessFilesAsync(MediaItem item, string folder, CancellationToken cancellationToken)
    {
        var original = Path.Combine(folder, "original" + Path.GetExtension(item.OriginalKey));
        string sha256;
        await using (var file = File.Create(original))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using var hashing = new HashingStream(file, hash);
            await storage.DownloadOriginalAsync(item.OriginalKey, hashing, cancellationToken);
            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        var probe = await transcoder.ProbeAsync(original, cancellationToken);
        if (!probe.HasVideo)
        {
            return await FailAsync(item, "This file has no picture. Upload a video or photo.", cancellationToken);
        }

        var playback = Path.Combine(folder, item.MediaType == MediaType.Video ? MediaKeys.VideoPlaybackFile : MediaKeys.ImageFile);
        var thumbnail = Path.Combine(folder, item.MediaType == MediaType.Video ? MediaKeys.VideoThumbnailFile : MediaKeys.ImageThumbnailFile);
        var manual = item.ManualRedactions;
        string playbackType;
        string thumbnailType;
        RedactionResult redaction;

        if (item.MediaType == MediaType.Video)
        {
            if (probe.DurationSeconds is { } seconds && seconds > _limits.MaxVideoDurationSeconds)
            {
                return await FailAsync(
                    item,
                    $"Videos can be up to {_limits.MaxVideoDurationSeconds / 60} minutes long. Trim it and upload again.",
                    cancellationToken);
            }

            redaction = await redactor.RedactVideoAsync(original, probe, manual, playback, cancellationToken);
            await transcoder.CreateVideoThumbnailAsync(playback, thumbnail, cancellationToken);
            (playbackType, thumbnailType) = ("video/mp4", "image/jpeg");
        }
        else
        {
            var redacted = Path.Combine(folder, "redacted.ppm");
            redaction = await redactor.RedactImageAsync(original, manual, redacted, cancellationToken);
            await transcoder.ConvertImageAsync(redacted, playback, thumbnail, cancellationToken);
            (playbackType, thumbnailType) = ("image/webp", "image/webp");
        }

        var reasons = redaction.ReviewReasons.ToList();
        if (manual.Count > 0)
        {
            reasons.Add($"A moderator added {manual.Count} blur region(s); check the result.");
        }

        var duration = item.MediaType == MediaType.Video ? probe.DurationSeconds : null;
        item.RecordRedaction(redaction.FacesBlurred, redaction.PlatesBlurred);
        string playbackKey;
        string thumbnailKey;
        if (reasons.Count > 0)
        {
            // Not public until a moderator approves: the blurred files wait in the private bucket.
            playbackKey = MediaKeys.ReviewPlayback(item.UserId, item.Id, item.MediaType, item.Revision);
            thumbnailKey = MediaKeys.ReviewThumbnail(item.UserId, item.Id, item.MediaType, item.Revision);
            await storage.UploadPrivateAsync(playbackKey, playback, playbackType, cancellationToken);
            await storage.UploadPrivateAsync(thumbnailKey, thumbnail, thumbnailType, cancellationToken);
            item.MarkNeedsReview(string.Join(" ", reasons), new FileInfo(playback).Length, duration, probe.Width, probe.Height, sha256, clock.GetUtcNow());
        }
        else
        {
            playbackKey = MediaKeys.Playback(item.UserId, item.Id, item.MediaType, item.Revision);
            thumbnailKey = MediaKeys.Thumbnail(item.UserId, item.Id, item.MediaType, item.Revision);
            await storage.UploadPublicAsync(playbackKey, playback, playbackType, cancellationToken);
            await storage.UploadPublicAsync(thumbnailKey, thumbnail, thumbnailType, cancellationToken);
            item.MarkReady(new FileInfo(playback).Length, duration, probe.Width, probe.Height, sha256, clock.GetUtcNow());
        }

        try
        {
            await media.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
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

            return ProcessingOutcome.Completed;
        }

        logger.LogInformation(
            "Media {MediaId} is {Status}: {Faces} faces and {Plates} plates blurred in {Elapsed} ({Frames} frames).",
            item.Id,
            item.Status,
            redaction.FacesBlurred,
            redaction.PlatesBlurred,
            redaction.Elapsed,
            redaction.Frames);
        return ProcessingOutcome.Completed;
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
