using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Notifications.Infrastructure;

/// <summary>
/// Deletes notifications older than <see cref="Notification.KeepFor"/> (90 days) every
/// <c>Notifications:CleanupInterval</c>. One set-based delete: safe to run on several replicas at once.
/// </summary>
internal sealed class NotificationsCleanup(
    IServiceScopeFactory scopes,
    IOptions<NotificationsOptions> options,
    TimeProvider clock,
    ILogger<NotificationsCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var notifications = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
                var deleted = await notifications.DeleteOlderThanAsync(clock.GetUtcNow() - Notification.KeepFor, stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Deleted {Count} old notifications.", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification clean-up failed; trying again at the next interval.");
            }

            await Delay(options.Value.CleanupInterval, stoppingToken);
        }
    }

    private Task Delay(TimeSpan wait, CancellationToken stoppingToken) =>
        Task.Delay(wait, clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
}
