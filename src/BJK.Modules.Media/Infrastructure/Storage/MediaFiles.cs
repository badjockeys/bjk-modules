using BJK.Modules.Media.Domain;

namespace BJK.Modules.Media.Infrastructure.Storage;

internal static class MediaFiles
{
    /// <summary>Removes the original and any processed files of one piece of media.</summary>
    public static async Task DeleteAllAsync(IMediaStorage storage, MediaItem item, CancellationToken cancellationToken)
    {
        await storage.DeleteOriginalAsync(item.OriginalKey, cancellationToken);
        if (item.Status == MediaStatus.NeedsReview)
        {
            await DeleteReviewFilesAsync(storage, item, cancellationToken);
        }

        var published = new[] { item.PlaybackKey, item.ThumbnailKey }.OfType<string>().ToList();
        if (published.Count > 0)
        {
            await storage.DeletePublicAsync(published, cancellationToken);
        }
    }

    /// <summary>Removes the blurred files kept privately for a moderator to review (current revision).</summary>
    public static async Task DeleteReviewFilesAsync(IMediaStorage storage, MediaItem item, CancellationToken cancellationToken)
    {
        await storage.DeleteOriginalAsync(MediaKeys.ReviewPlayback(item.UserId, item.Id, item.MediaType, item.Revision), cancellationToken);
        await storage.DeleteOriginalAsync(MediaKeys.ReviewThumbnail(item.UserId, item.Id, item.MediaType, item.Revision), cancellationToken);
    }
}
