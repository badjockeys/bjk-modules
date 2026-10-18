using System.Security.Cryptography;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
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
/// Turns one uploaded original into the published files: downloads it, checks it, converts it with ffmpeg (stripping
/// metadata such as GPS), uploads the results to the public bucket and marks the media Ready.
/// </summary>
internal sealed class MediaProcessor(
    IMediaRepository media,
    IMediaStorage storage,
    IMediaTranscoder transcoder,
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

        var playbackKey = MediaKeys.Playback(item.UserId, item.Id, item.MediaType);
        var thumbnailKey = MediaKeys.Thumbnail(item.UserId, item.Id, item.MediaType);
        var playback = Path.Combine(folder, Path.GetFileName(playbackKey));
        var thumbnail = Path.Combine(folder, Path.GetFileName(thumbnailKey));
        string playbackType;
        string thumbnailType;

        if (item.MediaType == MediaType.Video)
        {
            if (probe.DurationSeconds is { } seconds && seconds > _limits.MaxVideoDurationSeconds)
            {
                return await FailAsync(
                    item,
                    $"Videos can be up to {_limits.MaxVideoDurationSeconds / 60} minutes long. Trim it and upload again.",
                    cancellationToken);
            }

            await transcoder.TranscodeVideoAsync(original, playback, thumbnail, cancellationToken);
            (playbackType, thumbnailType) = ("video/mp4", "image/jpeg");
        }
        else
        {
            await transcoder.ConvertImageAsync(original, playback, thumbnail, cancellationToken);
            (playbackType, thumbnailType) = ("image/webp", "image/webp");
        }

        await storage.UploadPublicAsync(playbackKey, playback, playbackType, cancellationToken);
        await storage.UploadPublicAsync(thumbnailKey, thumbnail, thumbnailType, cancellationToken);

        item.MarkReady(
            new FileInfo(playback).Length,
            item.MediaType == MediaType.Video ? probe.DurationSeconds : null,
            probe.Width,
            probe.Height,
            sha256,
            clock.GetUtcNow());

        try
        {
            await media.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The owner deleted it while we were working: remove what we just published.
            logger.LogInformation("Media {MediaId} changed while processing; removing its published files.", item.Id);
            await storage.DeletePublicAsync([playbackKey, thumbnailKey], CancellationToken.None);
            return ProcessingOutcome.Completed;
        }

        logger.LogInformation(
            "Media {MediaId} is ready ({Type}, {Width}x{Height}, {Seconds}s).",
            item.Id,
            item.MediaType,
            probe.Width,
            probe.Height,
            probe.DurationSeconds);
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
