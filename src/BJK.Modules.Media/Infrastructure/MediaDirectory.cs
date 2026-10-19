using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure;

internal sealed class MediaDirectory(
    IMediaRepository media,
    IMediaStorage storage,
    IMediaJobQueue queue,
    IOptions<MediaOptions> options,
    TimeProvider clock,
    ILogger<MediaDirectory> logger,
    OriginalsCleanup originals) : IMediaDirectory
{
    public Task ReleaseOriginalsAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken) =>
        originals.ReleaseAsync(mediaIds, cancellationToken);

    public async Task<IReadOnlyList<MediaSummary>> GetAsync(
        IReadOnlyCollection<Guid> mediaIds,
        CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return [];
        }

        var items = await media.GetManyAsync(mediaIds, cancellationToken);
        var marks = await media.GetSensitiveMarksAsync(mediaIds, cancellationToken);
        return items.Select(item => ToSummary(item, MediaLinks.Public(storage, item), marks)).ToList();
    }

    public async Task<IReadOnlyList<MediaSummary>> GetWithSensitiveLinksAsync(
        IReadOnlyCollection<Guid> mediaIds,
        CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return [];
        }

        var items = await media.GetManyAsync(mediaIds, cancellationToken);
        var marks = await media.GetSensitiveMarksAsync(mediaIds, cancellationToken);
        var now = clock.GetUtcNow();
        var summaries = new List<MediaSummary>(items.Count);
        foreach (var item in items)
        {
            var links = item.Status == MediaStatus.Ready
                ? await MediaLinks.WithSensitiveAsync(storage, item, options.Value.SensitiveLinkLifetime, now, cancellationToken)
                : MediaFileLinks.None;
            summaries.Add(ToSummary(item, links, marks));
        }

        return summaries;
    }

    public async Task SetSensitiveAsync(IReadOnlyCollection<Guid> mediaIds, bool isSensitive, CancellationToken cancellationToken, bool clearAutomatic = false)
    {
        if (mediaIds.Count == 0)
        {
            return;
        }

        var existing = await media.GetUntrackedAsync(mediaIds, cancellationToken);
        var changed = new HashSet<Guid>();
        foreach (var item in existing)
        {
            if (await media.SetSensitiveAsync(item.Id, isSensitive, clock.GetUtcNow(), cancellationToken, clearAutomatic: clearAutomatic))
            {
                changed.Add(item.Id);
            }
        }

        // Read again after marking, so media a worker finished meanwhile is seen as Ready. A worker that finishes later
        // sees the mark itself. A change always queues the job (it does nothing unless the media is Ready); a repeat
        // queues it again only if Ready media's files still don't match (e.g. an earlier job failed for good).
        var items = await media.GetUntrackedAsync(mediaIds, cancellationToken);
        var marks = await media.GetSensitiveMarksAsync(mediaIds, cancellationToken);
        foreach (var item in items)
        {
            // What should be true now: an automatic mark may remain after clearing.
            var sensitive = marks.ContainsKey(item.Id);
            var filesMatch = item.IsRestricted == sensitive && (!sensitive || item.PreviewKey is not null);
            var finished = item.Status is MediaStatus.Failed or MediaStatus.Deleted;
            if (!finished && (changed.Contains(item.Id) || (item.Status == MediaStatus.Ready && !filesMatch)))
            {
                await queue.EnqueueAsync(MediaProcessingJob.ForVisibility(item), cancellationToken);
                logger.LogInformation(
                    "Media {MediaId} {Change}; queued moving its files.",
                    item.Id,
                    sensitive ? "marked sensitive" : "no longer sensitive");
            }
        }
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

    private MediaSummary ToSummary(MediaItem item, MediaFileLinks links, IReadOnlyDictionary<Guid, SensitiveMediaMark> marks)
    {
        var automatic = marks.TryGetValue(item.Id, out var mark) && mark.Source == SensitiveMarkSource.Automatic;
        var ready = item.Status == MediaStatus.Ready;
        return new MediaSummary(
            item.Id,
            item.UserId,
            item.MediaType,
            item.Status,
            ready ? links.Playback : null,
            ready ? links.Thumbnail : null,
            item.DurationSeconds,
            item.Width,
            item.Height,
            item.Status == MediaStatus.Failed ? item.FailureReason : null,
            ready ? MediaLinks.Preview(storage, item) : null,
            ready && item.IsRestricted,
            ready ? links.ExpiresAt : null,
            automatic,
            item.SensitiveScore);
    }
}
