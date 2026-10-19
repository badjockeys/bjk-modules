using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>
/// Visibility jobs: bring Ready media's files into line with its sensitive mark (<c>media.SensitiveMedia</c>).
/// </summary>
/// <remarks>
/// <para>
/// Sensitive: the real files are copied to the sensitive bucket (if they aren't there already), the blurred preview is
/// made and published, the media is saved as restricted, and only then are the public copies deleted and a CDN purge
/// requested. Not sensitive: the files are copied back to the public bucket (if missing), the media is saved as public,
/// and the preview is deleted. The private copies are kept, so files can never be lost between two jobs racing each
/// other (tag then untag); they're removed with the media.
/// </para>
/// <para>
/// Every step is safe to repeat and works from the current state, never from what the job was queued for, so a job
/// that crashes half way is simply run again, and late or duplicate jobs do no harm. If someone else changes the media
/// meanwhile, the save fails on its row version and the job starts again from the fresh state. Public copies are only
/// deleted after a fresh read shows the media still marked sensitive.
/// </para>
/// </remarks>
internal sealed partial class MediaProcessor
{
    private const int MaxVisibilityRounds = 5;

    private async Task<ProcessingOutcome> ReconcileVisibilityAsync(MediaProcessingJob job, CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(job.MediaId, cancellationToken);
            return ProcessingOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never marks the media Failed: it's Ready. After the last attempt the queue keeps the job in its failed
            // queue, where it can be replayed.
            logger.LogError(ex, "Moving the files of media {MediaId} for its sensitive tag failed; it will be retried.", job.MediaId);
            return ProcessingOutcome.Retry;
        }
    }

    /// <summary>
    /// After processing finished (Ready): if the sensitive mark changed while we worked, move the files now. If that
    /// fails, a visibility job is queued instead, since a repeat of the processing job would skip Ready media.
    /// </summary>
    private async Task CatchUpVisibilityAsync(MediaItem item, bool publishedAsSensitive, CancellationToken cancellationToken)
    {
        try
        {
            if (await media.IsSensitiveAsync(item.Id, cancellationToken) == publishedAsSensitive)
            {
                return;
            }

            logger.LogInformation("Media {MediaId}'s sensitive tag changed while it was processing; moving its files.", item.Id);
            await ReconcileAsync(item.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't check or move the files of media {MediaId} after processing; queueing a visibility job.", item.Id);
            try
            {
                await queue.EnqueueAsync(MediaProcessingJob.ForVisibility(item), CancellationToken.None);
            }
            catch (Exception queueException)
            {
                logger.LogError(queueException, "Couldn't queue a visibility job for media {MediaId}; its files may not match its sensitive tag.", item.Id);
            }
        }
    }

    /// <summary>Moves Ready media's files to match its sensitive mark. Does nothing for media in any other state.</summary>
    /// <param name="mediaId">The media.</param>
    /// <param name="cancellationToken">Stops the job.</param>
    internal async Task ReconcileAsync(Guid mediaId, CancellationToken cancellationToken)
    {
        var item = await media.GetByIdAsync(mediaId, cancellationToken);
        for (var round = 0; round < MaxVisibilityRounds; round++)
        {
            if (item is null || item.Status != MediaStatus.Ready || item.PlaybackKey is null || item.ThumbnailKey is null)
            {
                // Processing or waiting for review (that step checks the mark itself), failed, or deleted.
                logger.LogInformation("Media {MediaId} isn't Ready; its files are moved when it is.", mediaId);
                return;
            }

            var sensitive = await media.IsSensitiveAsync(item.Id, cancellationToken);
            var done = sensitive
                ? await RestrictFilesAsync(item, cancellationToken)
                : await PublishFilesAsync(item, cancellationToken);
            if (done)
            {
                return;
            }

            // Changed by someone else, or the mark changed again: start over from what's saved now.
            if (item.Status != MediaStatus.Deleted)
            {
                await media.ReloadAsync(item, cancellationToken);
            }

            item = await media.GetByIdAsync(mediaId, cancellationToken);
        }

        throw new InvalidOperationException($"Media {mediaId} kept changing while its files were being moved.");
    }

    /// <summary>The media is marked sensitive: private copies, blurred preview, save, then take the public copies down.</summary>
    /// <returns>True when done; false to start again (a save clashed or the mark changed).</returns>
    private async Task<bool> RestrictFilesAsync(MediaItem item, CancellationToken cancellationToken)
    {
        var (playbackType, thumbnailType) = MediaFiles.ContentTypes(item.MediaType);
        var files = ProcessedFiles(item, playbackType, thumbnailType);

        // 1. Private copies. Public is the source while the media isn't restricted yet; once it is, the copies already exist.
        foreach (var (key, type) in files)
        {
            var copy = MediaKeys.Sensitive(key);
            if (await storage.ExistsAsync(MediaBucket.Sensitive, copy, cancellationToken))
            {
                continue;
            }

            if (!await storage.ExistsAsync(MediaBucket.Public, key, cancellationToken))
            {
                throw new InvalidOperationException($"Media {item.Id} has neither a public nor a private copy of {Path.GetFileName(key)}.");
            }

            await storage.CopyAsync(MediaBucket.Public, key, MediaBucket.Sensitive, copy, type, cancellationToken);
        }

        // 2. The blurred preview, made from the private copy of the (already blurred) thumbnail.
        var previewKey = MediaKeys.Preview(item.UserId, item.Id, item.Revision);
        if (item.PreviewKey != previewKey || !await storage.ExistsAsync(MediaBucket.Public, previewKey, cancellationToken))
        {
            await InTemporaryFolderAsync(item.Id, "-preview", async folder =>
            {
                var thumbnail = Path.Combine(folder, Path.GetFileName(item.ThumbnailKey!));
                await using (var file = File.Create(thumbnail))
                {
                    await storage.DownloadAsync(MediaBucket.Sensitive, MediaKeys.Sensitive(item.ThumbnailKey!), file, cancellationToken);
                }

                await UploadPreviewAsync(thumbnail, previewKey, cancellationToken);
            });
        }

        // 3. Record that the files are private now.
        if (!item.IsRestricted || item.PreviewKey != previewKey)
        {
            item.Restrict(previewKey, clock.GetUtcNow());
            if (!await TrySaveAsync(item, cancellationToken))
            {
                return false;
            }
        }

        // 4. Take the public copies down, but only if it's still marked sensitive: if it was untagged meanwhile, go round again.
        if (!await media.IsSensitiveAsync(item.Id, cancellationToken))
        {
            return false;
        }

        var publicKeys = files.Select(f => f.Key).ToList();
        await storage.DeleteAsync(MediaBucket.Public, publicKeys, cancellationToken);
        await _purger.PurgeAsync(item.Id, publicKeys.Select(storage.PublicUrl).ToList(), cancellationToken);
        logger.LogInformation("Media {MediaId} is sensitive: its files are private and only the blurred preview is public.", item.Id);
        return true;
    }

    /// <summary>The media isn't marked sensitive: make sure the public copies are there, save, then remove the preview.</summary>
    /// <returns>True when done; false to start again (a save clashed or the mark changed).</returns>
    private async Task<bool> PublishFilesAsync(MediaItem item, CancellationToken cancellationToken)
    {
        var (playbackType, thumbnailType) = MediaFiles.ContentTypes(item.MediaType);
        var files = ProcessedFiles(item, playbackType, thumbnailType);

        // 1. Public copies, from the private ones. Also repairs public files deleted by a tag job that raced an untag.
        foreach (var (key, type) in files)
        {
            if (await storage.ExistsAsync(MediaBucket.Public, key, cancellationToken))
            {
                continue;
            }

            var copy = MediaKeys.Sensitive(key);
            if (await storage.ExistsAsync(MediaBucket.Sensitive, copy, cancellationToken))
            {
                await storage.CopyAsync(MediaBucket.Sensitive, copy, MediaBucket.Public, key, type, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException($"Media {item.Id} has neither a public nor a private copy of {Path.GetFileName(key)}.");
            }
        }

        // 2. Record that the files are public again.
        var preview = item.PreviewKey;
        if (item.IsRestricted || preview is not null)
        {
            item.Unrestrict(clock.GetUtcNow());
            if (!await TrySaveAsync(item, cancellationToken))
            {
                return false;
            }
        }

        // 3. Tagged again meanwhile? Go round again (the public copies come down there).
        if (await media.IsSensitiveAsync(item.Id, cancellationToken))
        {
            return false;
        }

        if (preview is not null)
        {
            await storage.DeleteAsync(MediaBucket.Public, [preview], cancellationToken);
            logger.LogInformation("Media {MediaId} is no longer sensitive: its files are public again.", item.Id);
        }

        return true;
    }

    /// <summary>The processed files that move between buckets: main file, thumbnail and (photos) the feed-size copy.</summary>
    private static List<(string Key, string Type)> ProcessedFiles(MediaItem item, string playbackType, string thumbnailType)
    {
        var files = new List<(string Key, string Type)> { (item.PlaybackKey!, playbackType), (item.ThumbnailKey!, thumbnailType) };
        if (item.FeedKey is not null)
        {
            files.Add((item.FeedKey, "image/webp"));
        }

        return files;
    }

    /// <summary>Saves, returning false (instead of throwing) if someone else changed the media first.</summary>
    private async Task<bool> TrySaveAsync(MediaItem item, CancellationToken cancellationToken)
    {
        try
        {
            await media.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Media {MediaId} changed while its files were being moved; starting again.", item.Id);
            return false;
        }
    }

    /// <summary>Runs some file work in a temporary folder of its own, removed afterwards.</summary>
    private async Task InTemporaryFolderAsync(Guid mediaId, string suffix, Func<string, Task> work)
    {
        var folder = Path.Combine(_options.WorkingDirectory ?? Path.GetTempPath(), "bjk-media", mediaId.ToString("N") + suffix);
        Directory.CreateDirectory(folder);
        try
        {
            await work(folder);
        }
        finally
        {
            TryDelete(folder);
        }
    }
}
