using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared.Uploads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure;

/// <summary>
/// Deletes uploads nothing uses, <c>Media:UnusedOriginalRetention</c> (24 hours) after upload (owner, 21 Oct 2026). The
/// apps upload a photo or video as soon as it's chosen, so closing the tab, removing a photo from the composer or never
/// posting leaves the upload behind: its original was already deleted after 24 hours, and now its processed files and
/// record go too. Every <see cref="IMediaUsage"/> (Posts for posts and drafts, Profiles for avatars) is asked which
/// candidates are in use; those are marked (<see cref="MediaItem.UsageCheckedAt"/>) and never checked again, because
/// whatever later removes them from their post, draft or avatar deletes them. The rest are deleted like a user's own
/// delete: files first, then the record is marked Deleted (kept for audit).
/// </summary>
/// <remarks>
/// Without any <see cref="IMediaUsage"/> registered (a host without the Posts and Profiles modules) nothing is deleted,
/// because every upload would look unused. Someone posting an upload more than 24 hours after choosing it, in a tab
/// left open without saving a draft, finds it gone and is asked to add it again.
/// </remarks>
internal sealed class UnusedMediaCleanup(
    IMediaRepository media,
    IMediaStorage storage,
    IEnumerable<IMediaUsage> usages,
    IOptions<MediaOptions> options,
    TimeProvider clock,
    ILogger<UnusedMediaCleanup> logger)
{
    /// <summary>How many uploads one sweep looks at, at most.</summary>
    public const int SweepBatchSize = 200;

    private readonly IReadOnlyList<IMediaUsage> _usages = usages.ToList();

    /// <summary>One sweep. Returns how many uploads it looked at (deleted or marked in use).</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        if (_usages.Count == 0)
        {
            logger.LogWarning("No module reports which uploads are in use, so unused uploads aren't deleted.");
            return 0;
        }

        var now = clock.GetUtcNow();
        var candidates = await media.ListUnusedCandidatesAsync(now - options.Value.UnusedOriginalRetention, SweepBatchSize, cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var ids = candidates.Select(m => m.Id).ToList();
        var inUse = new HashSet<Guid>();
        foreach (var usage in _usages)
        {
            inUse.UnionWith(await usage.InUseAsync(ids, cancellationToken));
        }

        var unused = new List<MediaItem>();
        foreach (var item in candidates)
        {
            if (inUse.Contains(item.Id))
            {
                item.MarkInUse(now);
            }
            else
            {
                // Files first: if a delete fails the record is untouched and the next sweep tries again.
                await MediaFiles.DeleteAllAsync(storage, item, cancellationToken);
                item.Delete(now);
                unused.Add(item);
            }
        }

        try
        {
            await media.SaveChangesAsync(cancellationToken);
            if (unused.Count > 0)
            {
                logger.LogInformation(
                    "Deleted {Count} upload(s) nothing used, over {Hours} hours old.",
                    unused.Count,
                    options.Value.UnusedOriginalRetention.TotalHours);
            }
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Changed meanwhile (e.g. deleted by its owner); the next sweep looks at whatever is left.
            logger.LogInformation(ex, "Some uploads changed while unused ones were being deleted.");
        }

        return candidates.Count;
    }
}
