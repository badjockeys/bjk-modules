using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>
/// Share copy jobs: the vertical copy of a posted video for TikTok, Reels, Shorts and X (<see cref="ShareCopy"/>). Made
/// from the published file, which is already blurred and silent, never from the original. Failing never touches the
/// media: only the copy is marked Failed, and its owner can ask again.
/// </summary>
internal sealed partial class MediaProcessor
{
    private async Task<ProcessingOutcome> MakeShareCopyAsync(MediaProcessingJob job, bool isLastAttempt, CancellationToken cancellationToken)
    {
        if (shareCopies is null)
        {
            throw new InvalidOperationException("Share copies need IShareCopyStore registered.");
        }

        var copy = await shareCopies.GetAsync(job.MediaId, cancellationToken);
        var item = await media.GetByIdAsync(job.MediaId, cancellationToken);
        if (copy is null || copy.Status != ShareCopyStatus.Rendering || copy.Revision != job.Revision)
        {
            // Already made by an earlier delivery, or asked for again since (a newer job does it).
            return ProcessingOutcome.Completed;
        }

        if (item is null
            || item.MediaType != MediaType.Video
            || item.Status != MediaStatus.Ready
            || item.IsRestricted
            || item.PlaybackKey is null
            || item.Revision != copy.Revision)
        {
            // Deleted, re-processed, or tagged sensitive since it was asked for: nothing to share.
            logger.LogInformation("Not making a share copy of media {MediaId}: it can't be shared now.", job.MediaId);
            copy.MarkFailed();
            await shareCopies.SaveChangesAsync(cancellationToken);
            return ProcessingOutcome.Completed;
        }

        var folder = Path.Combine(_options.WorkingDirectory ?? Path.GetTempPath(), "bjk-media", item.Id.ToString("N") + "-share");
        Directory.CreateDirectory(folder);
        try
        {
            var playback = Path.Combine(folder, "playback.mp4");
            await using (var file = File.Create(playback))
            {
                await storage.DownloadAsync(MediaBucket.Public, item.PlaybackKey, file, cancellationToken);
            }

            var (watermark, endCard) = await ShareVideoAssets.WriteToAsync(folder, cancellationToken);
            var output = Path.Combine(folder, "vertical.mp4");
            await transcoder.CreateShareVideoAsync(playback, watermark, endCard, output, cancellationToken);

            var key = MediaKeys.ShareVideo(item.UserId, item.Id);
            await storage.UploadAsync(MediaBucket.Originals, key, output, "video/mp4", cancellationToken);
            copy.MarkReady(key, clock.GetUtcNow());
            await shareCopies.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Made the share copy of media {MediaId}.", item.Id);
            return ProcessingOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (isLastAttempt)
        {
            logger.LogError(ex, "Making the share copy of media {MediaId} failed on its last attempt.", item.Id);
            await FailShareCopyAsync(item.Id, CancellationToken.None);
            return ProcessingOutcome.Failed;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Making the share copy of media {MediaId} failed; it will be retried.", item.Id);
            return ProcessingOutcome.Retry;
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private async Task FailShareCopyAsync(Guid mediaId, CancellationToken cancellationToken)
    {
        if (shareCopies is null)
        {
            return;
        }

        try
        {
            var copy = await shareCopies.GetAsync(mediaId, cancellationToken);
            if (copy is { Status: ShareCopyStatus.Rendering })
            {
                copy.MarkFailed();
                await shareCopies.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // It shows as lost after ShareCopy.StaleAfter anyway, and can be asked for again.
            logger.LogWarning(ex, "Couldn't mark the share copy of media {MediaId} as failed.", mediaId);
        }
    }
}
