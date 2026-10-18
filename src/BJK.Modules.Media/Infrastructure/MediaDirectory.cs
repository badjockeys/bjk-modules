using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;

namespace BJK.Modules.Media.Infrastructure;

internal sealed class MediaDirectory(IMediaRepository media, IMediaStorage storage, TimeProvider clock) : IMediaDirectory
{
    public async Task<IReadOnlyList<MediaSummary>> GetAsync(
        IReadOnlyCollection<Guid> mediaIds,
        CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return [];
        }

        var items = await media.GetManyAsync(mediaIds, cancellationToken);
        return items.Select(ToSummary).ToList();
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return;
        }

        var items = await media.GetManyAsync(mediaIds, cancellationToken);
        foreach (var item in items)
        {
            await MediaFiles.DeleteAllAsync(storage, item, cancellationToken);
            item.Delete(clock.GetUtcNow());
        }

        await media.SaveChangesAsync(cancellationToken);
    }

    private MediaSummary ToSummary(MediaItem item)
    {
        var ready = item.Status == MediaStatus.Ready;
        return new MediaSummary(
            item.Id,
            item.UserId,
            item.MediaType,
            item.Status,
            ready && item.PlaybackKey is not null ? storage.PublicUrl(item.PlaybackKey) : null,
            ready && item.ThumbnailKey is not null ? storage.PublicUrl(item.ThumbnailKey) : null,
            item.DurationSeconds,
            item.Width,
            item.Height);
    }
}
