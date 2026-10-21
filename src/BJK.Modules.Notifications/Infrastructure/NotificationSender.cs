using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Email.Templates;
using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Notifications.Infrastructure.Push;
using BJK.Modules.Notifications.UseCases;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Notifications.Infrastructure;

/// <summary>
/// Saves a notification for the bell, emails it and pushes it to the person's devices, each only if their settings
/// allow (see <see cref="NotificationDefaults"/>). Nobody is notified about their own actions. A reaction to a post that
/// already has an unread reaction notification is added to it (not emailed again, and pushed at most hourly). Nothing is
/// pushed during <see cref="PushQuietHours"/>. Emails go to the confirmed address the person's token last gave
/// (<see cref="NotificationContact"/>); without one, there's no email. Never throws: a failure is logged and the action
/// that caused it stands.
/// </summary>
internal sealed class NotificationSender(
    INotificationRepository notifications,
    IProfileDirectory profiles,
    IEmailSender email,
    IPushSender push,
    IOptions<EmailOptions> emailOptions,
    IOptions<NotificationsOptions> options,
    TimeProvider clock,
    ILogger<NotificationSender> logger) : INotificationSender
{
    public async Task NotifyAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        if (request.UserId == Guid.Empty || request.ActorId == request.UserId)
        {
            return;
        }

        try
        {
            var chosen = (await notifications.GetSettingsAsync(request.UserId, cancellationToken))
                .FirstOrDefault(s => s.Type == request.Type);
            var (inApp, byEmail, byPush) = chosen is null
                ? NotificationDefaults.For(request.Type)
                : (chosen.InApp, chosen.Email, chosen.Push);
            await DeliverAsync(request, inApp, byEmail, null, cancellationToken, byPush: byPush);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A {Type} notification for user {UserId} couldn't be sent.", request.Type, request.UserId);
        }
    }

    /// <summary>
    /// Puts the notification in the bell (<paramref name="inApp"/>), emails it (<paramref name="byEmail"/>) and pushes
    /// it (<paramref name="byPush"/>), with <paramref name="template"/> instead of the standard email if given (area
    /// summaries list their posts). Throws if the bell or email fails (callers log it); a failed push is only logged.
    /// </summary>
    internal async Task DeliverAsync(
        NotificationRequest request,
        bool inApp,
        bool byEmail,
        EmailTemplate? template,
        CancellationToken cancellationToken,
        int count = 1,
        bool byPush = false)
    {
        if (!inApp && !byEmail && !byPush)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var grouped = false;
        var shouldPush = byPush && push.IsEnabled && !PushQuietHours.IsQuiet(now);
        var people = count;
        if (inApp)
        {
            Notification target;
            if (request.Type == NotificationType.ReactionOnPost && request.PostId is { } postId
                && await notifications.FindUnreadAsync(request.UserId, request.Type, postId, cancellationToken) is { } existing)
            {
                existing.AddOne(request.ActorId, now);
                grouped = true;
                target = existing;
            }
            else
            {
                target = Notification.Create(request, now);
                for (var i = 1; i < count; i++)
                {
                    target.AddOne(null, now);
                }

                notifications.Add(target);
            }

            people = target.Count;
            shouldPush = shouldPush && target.MayPush(now);
            if (shouldPush)
            {
                target.MarkPushed(now);
            }

            await notifications.SaveChangesAsync(cancellationToken);
        }

        string? actorName = null;
        var actorLooked = false;
        async Task<string?> ActorNameAsync()
        {
            if (!actorLooked && request.ActorId is { } actorId)
            {
                actorLooked = true;
                var found = await profiles.GetSummariesAsync([actorId], cancellationToken);
                actorName = found.TryGetValue(actorId, out var actor) ? actor.DisplayName : null;
            }

            return actorName;
        }

        if (byEmail && !grouped)
        {
            await EmailAsync(request, template, await ActorNameAsync(), cancellationToken);
        }

        if (shouldPush)
        {
            try
            {
                var text = NotificationTexts.Sentence(request.Type, await ActorNameAsync(), people, request.PostTitle, request.Detail);
                var url = request.PostId is { } id ? $"/p/{id:D}" : "/notifications";
                await push.SendAsync(
                    request.UserId,
                    new PushMessage("Bad Jockeys", text, url, $"{request.Type}:{request.PostId:D}"),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A {Type} push for user {UserId} couldn't be sent.", request.Type, request.UserId);
            }
        }
    }

    /// <summary>The web app's links, if <c>Notifications:AppUrl</c> is set.</summary>
    internal Uri? Link(string path) => options.Value.AppUrl is { } app ? new Uri(app, path) : null;

    private async Task EmailAsync(NotificationRequest request, EmailTemplate? custom, string? actorName, CancellationToken cancellationToken)
    {
        var contact = await notifications.GetContactAsync(request.UserId, cancellationToken);
        if (contact is null)
        {
            return;
        }

        if (custom is not null)
        {
            await email.SendAsync(custom.To(contact.Email, emailOptions.Value.LogoUrl), cancellationToken);
            return;
        }

        var link = request.PostId is null ? null : Link($"/p/{request.PostId:D}");
        var settings = Link("/account/notifications");
        var template = EmailTemplates.Notification(
            NotificationTexts.Subject(request.Type, actorName),
            NotificationTexts.Sentence(request.Type, actorName, 1, request.PostTitle, request.Detail),
            link,
            settings);
        await email.SendAsync(template.To(contact.Email, emailOptions.Value.LogoUrl), cancellationToken);
    }
}
