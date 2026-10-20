using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure;

/// <summary>
/// Deletes uploaded originals once they're no longer needed (owner's decision, 19 Oct 2026, replacing 90-day
/// retention): straight away when the post using the media is published (<see cref="ReleaseAsync"/>, called by the
/// Posts module through <c>IMediaDirectory.ReleaseOriginalsAsync</c>), and otherwise by the hourly sweep
/// (<see cref="SweepAsync"/>) at most <c>Media:UnusedOriginalRetention</c> (24 hours) after upload: failed media,
/// media never posted, abandoned uploads. Media still processing or waiting for a moderator keeps its original.
/// Deleting a file that's already gone is harmless, so both are safe to repeat.
/// </summary>
internal sealed class OriginalsCleanup(
    IMediaRepository media,
    IMediaStorage storage,
    IOptions<MediaOptions> options,
    TimeProvider clock,
    ILogger<OriginalsCleanup> logger)
{
    /// <summary>How many originals one sweep deletes at most.</summary>
    public const int SweepBatchSize = 200;

    /// <summary>Deletes the originals of these media if they're Ready (processed, or approved by a moderator).</summary>
    /// <param name="mediaIds">The media of a post that has just been published.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task ReleaseAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return;
        }

        var items = (await media.GetManyAsync(mediaIds, cancellationToken))
            .Where(m => m.Status == MediaStatus.Ready && m.OriginalDeletedAt is null)
            .ToList();
        await DeleteAsync(items, cancellationToken);
    }

    /// <summary>One sweep: deletes originals past their deadline. Returns how many were deleted.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var items = await media.ListOriginalsToDeleteAsync(now, now - options.Value.UnusedOriginalRetention, SweepBatchSize, cancellationToken);
        await DeleteAsync(items, cancellationToken);
        return items.Count;
    }

    private async Task DeleteAsync(IReadOnlyList<MediaItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            // File first, then the record: if saving fails the next sweep simply deletes nothing and records it.
            await storage.DeleteOriginalAsync(item.OriginalKey, cancellationToken);
            item.MarkOriginalDeleted(clock.GetUtcNow());
        }

        try
        {
            await media.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Deleted {Count} original upload(s) no longer needed.", items.Count);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Changed meanwhile (e.g. deleted); the files are gone either way and the next sweep records the rest.
            logger.LogInformation(ex, "Some media changed while their originals were being deleted.");
        }
    }
}

/// <summary>
/// Runs <see cref="OriginalsCleanup.SweepAsync"/>, then <see cref="UnusedMediaCleanup.SweepAsync"/>, every
/// <c>Media:OriginalSweepInterval</c> (1 hour) in the API host.
/// </summary>
internal sealed class OriginalsSweeper(IServiceScopeFactory scopes, IOptions<MediaOptions> options, ILogger<OriginalsSweeper> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting (and migrations run) first.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A full batch means there may be more: up to ten batches per interval, each in a fresh scope.
                for (var batch = 0; batch < 10; batch++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var cleanup = scope.ServiceProvider.GetRequiredService<OriginalsCleanup>();
                    if (await cleanup.SweepAsync(stoppingToken) < OriginalsCleanup.SweepBatchSize)
                    {
                        break;
                    }
                }

                // Then uploads nothing uses (not in a post, draft or avatar) after the same 24 hours.
                for (var batch = 0; batch < 10; batch++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var unused = scope.ServiceProvider.GetRequiredService<UnusedMediaCleanup>();
                    if (await unused.SweepAsync(stoppingToken) < UnusedMediaCleanup.SweepBatchSize)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sweeping unused originals and uploads failed; trying again at the next interval.");
            }

            await Task.Delay(options.Value.OriginalSweepInterval, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
