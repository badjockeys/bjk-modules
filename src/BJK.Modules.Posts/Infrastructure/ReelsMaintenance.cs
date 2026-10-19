using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>
/// Keeps reels running, in the API host: recomputes the ranking (<see cref="Post.ReelScore"/>) every
/// <c>Reels:RankingRefreshInterval</c> (10 minutes), and deletes old plays every <c>Reels:PlaySweepInterval</c>
/// (hourly): signed-in viewers' after <c>Reels:PlayRetention</c> (35 days), logged-out viewers' after
/// <c>Reels:AnonymousPlayRetention</c> (2 days). Daily play counts per post are kept. Both jobs are single set-based
/// statements and safe to run on several replicas at once (they'd do the same work twice, nothing worse).
/// </summary>
internal sealed class ReelsMaintenance(
    IServiceScopeFactory scopes,
    IOptions<ReelsOptions> options,
    TimeProvider clock,
    ILogger<ReelsMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting first.
        await Delay(TimeSpan.FromSeconds(15), stoppingToken);
        var nextSweep = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = options.Value;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var reels = scope.ServiceProvider.GetRequiredService<IReelRepository>();
                var now = clock.GetUtcNow();
                var scored = await reels.RefreshScoresAsync(now, now - settings.RankingWindow, stoppingToken);
                logger.LogDebug("Reels ranking refreshed for {Count} video posts.", scored);

                if (now >= nextSweep)
                {
                    var today = UkCalendar.Today(now);
                    var deleted = await reels.DeletePlaysBeforeAsync(
                        today.AddDays(-(int)Math.Ceiling(settings.PlayRetention.TotalDays) + 1),
                        today.AddDays(-(int)Math.Ceiling(settings.AnonymousPlayRetention.TotalDays) + 1),
                        stoppingToken);
                    if (deleted > 0)
                    {
                        logger.LogInformation("Deleted {Count} old reel plays.", deleted);
                    }

                    nextSweep = now + settings.PlaySweepInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reels maintenance failed; trying again at the next interval.");
            }

            await Delay(settings.RankingRefreshInterval, stoppingToken);
        }
    }

    private Task Delay(TimeSpan wait, CancellationToken stoppingToken) =>
        Task.Delay(wait, clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
}
