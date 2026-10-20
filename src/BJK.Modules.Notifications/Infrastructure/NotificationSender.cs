using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Email.Templates;
using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Notifications.UseCases;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Notifications.Infrastructure;

/// <summary>
/// Saves a notification for the bell and emails it, each only if the person's settings allow (see
/// <see cref="NotificationDefaults"/>). Nobody is notified about their own actions. A reaction to a post that already
/// has an unread reaction notification is added to it (and not emailed again). Emails go to the confirmed address the
/// person's token last gave (<see cref="NotificationContact"/>); without one, there's no email. Never throws: a
/// failure is logged and the action that caused it stands.
/// </summary>
internal sealed class NotificationSender(
    INotificationRepository notifications,
    IProfileDirectory profiles,
    IEmailSender email,
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
            var (inApp, byEmail) = chosen is null ? NotificationDefaults.For(request.Type) : (chosen.InApp, chosen.Email);
            await DeliverAsync(request, inApp, byEmail, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A {Type} notification for user {UserId} couldn't be sent.", request.Type, request.UserId);
        }
    }

    /// <summary>
    /// Puts the notification in the bell (<paramref name="inApp"/>) and emails it (<paramref name="byEmail"/>), with
    /// <paramref name="template"/> instead of the standard email if given (area summaries list their posts). Throws on
    /// failure: callers log it.
    /// </summary>
    internal async Task DeliverAsync(
        NotificationRequest request,
        bool inApp,
        bool byEmail,
        EmailTemplate? template,
        CancellationToken cancellationToken,
        int count = 1)
    {
        if (!inApp && !byEmail)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var grouped = false;
        if (inApp)
        {
            if (request.Type == NotificationType.ReactionOnPost && request.PostId is { } postId
                && await notifications.FindUnreadAsync(request.UserId, request.Type, postId, cancellationToken) is { } existing)
            {
                existing.AddOne(request.ActorId, now);
                grouped = true;
            }
            else
            {
                var notification = Notification.Create(request, now);
                for (var i = 1; i < count; i++)
                {
                    notification.AddOne(null, now);
                }

                notifications.Add(notification);
            }

            await notifications.SaveChangesAsync(cancellationToken);
        }

        if (byEmail && !grouped)
        {
            await EmailAsync(request, template, cancellationToken);
        }
    }

    /// <summary>The web app's links, if <c>Notifications:AppUrl</c> is set.</summary>
    internal Uri? Link(string path) => options.Value.AppUrl is { } app ? new Uri(app, path) : null;

    private async Task EmailAsync(NotificationRequest request, EmailTemplate? custom, CancellationToken cancellationToken)
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

        string? actorName = null;
        if (request.ActorId is { } actorId)
        {
            var people = await profiles.GetSummariesAsync([actorId], cancellationToken);
            actorName = people.TryGetValue(actorId, out var actor) ? actor.DisplayName : null;
        }

        var link = request.PostId is null ? null : Link($"/p/{request.PostId:D}");
        var settings = Link("/account/notifications");
        var template = EmailTemplates.Notification(
            NotificationTexts.Subject(request.Type, actorName),
            NotificationTexts.Sentence(request.Type, actorName, 1, request.PostTitle),
            link,
            settings);
        await email.SendAsync(template.To(contact.Email, emailOptions.Value.LogoUrl), cancellationToken);
    }
}
