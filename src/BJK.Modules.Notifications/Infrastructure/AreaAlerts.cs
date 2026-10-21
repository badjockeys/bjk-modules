using BJK.Modules.Email.Templates;
using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Notifications.Infrastructure;

/// <summary>
/// Premium area alerts. When a post goes public, everyone with alerts switched on whose home area is within their
/// radius hears about it (not the author, and not people who've blocked them or whom they've blocked): Instant
/// subscribers straight away, in the bell and as a push (up to 10 a day, never in quiet hours, 22:00 to 07:00 UK time), and everything else is
/// held for the next summary (<see cref="AreaSummaries"/>). Never throws.
/// </summary>
internal sealed class AreaAlerts(
    IAreaAlertRepository areas,
    IBlockDirectory blocks,
    NotificationSender sender,
    TimeProvider clock,
    ILogger<AreaAlerts> logger) : IAreaAlerts
{
    private const int LargestRadiusKm = 25;

    public async Task PostPublishedAsync(PublishedPost post, CancellationToken cancellationToken)
    {
        try
        {
            var now = clock.GetUtcNow();
            var nearby = (await areas.FindAroundAsync(post.Latitude, post.Longitude, LargestRadiusKm, cancellationToken))
                .Where(s => s.UserId != post.AuthorId && s.Active(now) && s.DistanceKm(post.Latitude, post.Longitude) <= s.RadiusKm)
                .ToList();
            if (nearby.Count == 0)
            {
                return;
            }

            var blockedByAuthor = (await blocks.GetBlockedEitherWayAsync(post.AuthorId, cancellationToken)).ToHashSet();
            var instant = new List<AreaSubscription>();
            foreach (var subscription in nearby.Where(s => !blockedByAuthor.Contains(s.UserId)))
            {
                if (subscription.TryTakeInstant(now))
                {
                    instant.Add(subscription);
                }
                else
                {
                    areas.AddItem(AreaAlertItem.Create(subscription.UserId, post.PostId, post.Title, post.PlaceName, now));
                }
            }

            await areas.SaveChangesAsync(cancellationToken);

            foreach (var subscription in instant)
            {
                var request = new NotificationRequest(subscription.UserId, NotificationType.AreaPost, PostId: post.PostId, PostTitle: post.Title);
                var email = subscription.Email
                    ? EmailTemplates.AreaSummary(
                        subscription.PlaceName,
                        [(post.Title, post.PlaceName, sender.Link($"/p/{post.PostId:D}"))],
                        sender.Link("/account/notifications"))
                    : null;
                await sender.DeliverAsync(request, inApp: true, byEmail: subscription.Email, email, cancellationToken, byPush: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Area alerts for post {PostId} couldn't be sent.", post.PostId);
        }
    }
}

/// <summary>
/// Sends area summaries every few minutes as they fall due: 08:00 UK time for Digest subscribers, 07:00 for Instant
/// subscribers' posts held overnight or over the daily cap. One bell notification ("5 new posts in your area,
/// including ...") and, if chosen, one email listing them. Posts held for people whose alerts are off or whose
/// Premium lapsed are dropped after two days.
/// </summary>
internal sealed class AreaSummaries(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<AreaSummaries> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Delay(TimeSpan.FromSeconds(45), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await SendDueAsync(
                    scope.ServiceProvider.GetRequiredService<IAreaAlertRepository>(),
                    scope.ServiceProvider.GetRequiredService<NotificationSender>(),
                    clock.GetUtcNow(),
                    logger,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Area summaries failed; trying again shortly.");
            }

            await Delay(Interval, stoppingToken);
        }
    }

    /// <summary>One pass: every summary that's due. Public for tests.</summary>
    internal static async Task SendDueAsync(
        IAreaAlertRepository areas,
        NotificationSender sender,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var subscription in await areas.ListWithWaitingAsync(cancellationToken))
        {
            if (!subscription.Active(now) || !subscription.SummaryDue(now))
            {
                continue;
            }

            var items = await areas.GetWaitingAsync(subscription.UserId, subscription.SummaryCutoff(now), cancellationToken);
            if (items.Count == 0)
            {
                continue;
            }

            var latest = items[^1];
            var request = new NotificationRequest(subscription.UserId, NotificationType.AreaPost, PostId: latest.PostId, PostTitle: latest.Title);
            var email = subscription.Email
                ? EmailTemplates.AreaSummary(
                    subscription.PlaceName,
                    items.Select(i => (i.Title, i.PlaceName, sender.Link($"/p/{i.PostId:D}"))).ToList(),
                    sender.Link("/account/notifications"))
                : null;
            try
            {
                await sender.DeliverAsync(request, inApp: true, byEmail: subscription.Email, email, cancellationToken, count: items.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The area summary for user {UserId} couldn't be sent.", subscription.UserId);
            }

            areas.RemoveItems(items);
            subscription.MarkSummarySent(now);
            await areas.SaveChangesAsync(cancellationToken);
        }

        await areas.DeleteItemsOlderThanAsync(now - TimeSpan.FromDays(2), cancellationToken);
    }

    private Task Delay(TimeSpan wait, CancellationToken stoppingToken) =>
        Task.Delay(wait, clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
}
