using BJK.Modules.Media.Domain;

namespace BJK.Modules.Media.Infrastructure.Storage;

internal static class MediaFiles
{
    /// <summary>Removes the original and any processed files of one piece of media.</summary>
    public static async Task DeleteAllAsync(IMediaStorage storage, MediaItem item, CancellationToken cancellationToken)
    {
        await storage.DeleteOriginalAsync(item.OriginalKey, cancellationToken);
        var published = new[] { item.PlaybackKey, item.ThumbnailKey }.OfType<string>().ToList();
        if (published.Count > 0)
        {
            await storage.DeletePublicAsync(published, cancellationToken);
        }
    }
}
