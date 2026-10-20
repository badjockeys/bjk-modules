using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Drafts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>
/// Every hour, deletes drafts not saved for <see cref="PostDraft.Lifetime"/> (30 days), with the photos and videos
/// only they were using.
/// </summary>
internal sealed class DraftsCleanup(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DraftsCleanup> logger)
    : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting first.
        await Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int deleted;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var drafts = scope.ServiceProvider.GetRequiredService<IDraftRepository>();
                    var deletion = scope.ServiceProvider.GetRequiredService<DraftDeletion>();
                    var expired = await drafts.ListExpiredAsync(clock.GetUtcNow() - PostDraft.Lifetime, BatchSize, stoppingToken);
                    deleted = expired.Count;
                    if (deleted > 0)
                    {
                        await deletion.DeleteAsync(expired, stoppingToken);
                        logger.LogInformation("Deleted {Count} expired drafts.", deleted);
                    }
                }
                while (deleted == BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deleting expired drafts failed; trying again at the next interval.");
            }

            await Delay(Interval, stoppingToken);
        }
    }

    private Task Delay(TimeSpan wait, CancellationToken stoppingToken) =>
        Task.Delay(wait, clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
}
