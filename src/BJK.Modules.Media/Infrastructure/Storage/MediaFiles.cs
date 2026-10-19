using BJK.Modules.Media.Domain;

namespace BJK.Modules.Media.Infrastructure.Storage;

internal static class MediaFiles
{
    /// <summary>Removes the original and any processed files of one piece of media.</summary>
    public static async Task DeleteAllAsync(IMediaStorage storage, MediaItem item, CancellationToken cancellationToken)
    {
        await storage.DeleteOriginalAsync(item.OriginalKey, cancellationToken);
        if (item.SourceKey is not null)
        {
            await storage.DeleteOriginalAsync(item.SourceKey, cancellationToken);
        }

        if (item.Status == MediaStatus.NeedsReview)
        {
            await DeleteReviewFilesAsync(storage, item, cancellationToken);
        }

        var published = new[] { item.PlaybackKey, item.ThumbnailKey, item.PreviewKey }.OfType<string>().ToList();
        if (published.Count > 0)
        {
            await storage.DeletePublicAsync(published, cancellationToken);
        }

        await DeleteSensitiveCopiesAsync(storage, item, cancellationToken);
    }

    /// <summary>
    /// Removes the private copies of the processed files kept for sensitive media (current revision), if any. They're
    /// kept even after the sensitive tag is removed, so they're removed whether or not the media is restricted now.
    /// </summary>
    public static async Task DeleteSensitiveCopiesAsync(IMediaStorage storage, MediaItem item, CancellationToken cancellationToken)
    {
        var copies = new[] { item.PlaybackKey, item.ThumbnailKey }.OfType<string>().Select(MediaKeys.Sensitive).ToList();
        if (copies.Count > 0)
        {
            await storage.DeleteAsync(MediaBucket.Sensitive, copies, cancellationToken);
        }
    }

    /// <summary>Content types of the processed main file and thumbnail.</summary>
    public static (string Playback, string Thumbnail) ContentTypes(MediaType type) =>
        type == MediaType.Video ? ("video/mp4", "image/jpeg") : ("image/webp", "image/webp");

    /// <summary>Removes the blurred files kept privately for a moderator to review (current revision).</summary>
    public static async Task DeleteReviewFilesAsync(IMediaStorage storage, MediaItem item, CancellationToken cancellationToken)
    {
        await storage.DeleteOriginalAsync(MediaKeys.ReviewPlayback(item.UserId, item.Id, item.MediaType, item.Revision), cancellationToken);
        await storage.DeleteOriginalAsync(MediaKeys.ReviewThumbnail(item.UserId, item.Id, item.MediaType, item.Revision), cancellationToken);
    }
}
