using BJK.Modules.Posts.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>
/// Keeps Trending's hourly counts current, in the API host: every <c>Hashtags:RebuildInterval</c> (5 minutes) the
/// last two days' hours are rebuilt from public posts (so posts hidden, held or deleted since drop out), and counts
/// older than <c>Hashtags:HourlyRetention</c> (90 days) are deleted. One set-based statement in a transaction, safe to
/// run on several replicas at once.
/// </summary>
internal sealed class HashtagMaintenance(
    IServiceScopeFactory scopes,
    IOptions<HashtagOptions> options,
    TimeProvider clock,
    ILogger<HashtagMaintenance> logger) : BackgroundService
{
    /// <summary>How far back the hourly counts are rebuilt each time.</summary>
    public static readonly TimeSpan RebuildWindow = TimeSpan.FromHours(48);

    /// <summary>The start of the UTC hour <paramref name="window"/> before <paramref name="now"/>.</summary>
    public static DateTimeOffset RebuildFrom(DateTimeOffset now, TimeSpan window)
    {
        var from = (now - window).ToUniversalTime();
        return new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, 0, 0, TimeSpan.Zero);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting first.
        await Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = options.Value;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var hashtags = scope.ServiceProvider.GetRequiredService<IHashtagRepository>();
                var now = clock.GetUtcNow();
                await hashtags.RebuildHourlyCountsAsync(RebuildFrom(now, RebuildWindow), now - settings.HourlyRetention, stoppingToken);
                logger.LogDebug("Hashtag hourly counts rebuilt.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rebuilding hashtag counts failed; trying again at the next interval.");
            }

            await Delay(settings.RebuildInterval, stoppingToken);
        }
    }

    private Task Delay(TimeSpan wait, CancellationToken stoppingToken) =>
        Task.Delay(wait, clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
}
