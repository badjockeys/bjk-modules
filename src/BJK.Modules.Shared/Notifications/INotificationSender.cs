using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BJK.Modules.Shared.Notifications;

/// <summary>What happened, for a notification. Stored by name: add new types at the end.</summary>
public enum NotificationType
{
    /// <summary>Someone tagged you as a passenger: accept or decline.</summary>
    PassengerTagged,

    /// <summary>Someone accepted your passenger tag.</summary>
    PassengerAccepted,

    /// <summary>Someone commented on your post.</summary>
    CommentOnPost,

    /// <summary>Someone replied to your comment.</summary>
    ReplyToComment,

    /// <summary>Someone reacted to your post (grouped per post while unread).</summary>
    ReactionOnPost,

    /// <summary>A new post in your home area (Premium area alerts).</summary>
    AreaPost,

    /// <summary>Someone started following you.</summary>
    NewFollower,
}

/// <summary>
/// One notification to send. <c>UserId</c> gets it; <c>ActorId</c> is who did it (never notified about their own
/// actions). <c>PostTitle</c> is kept with the notification, so it reads the same if the post is later edited.
/// </summary>
public sealed record NotificationRequest(
    Guid UserId,
    NotificationType Type,
    Guid? ActorId = null,
    Guid? PostId = null,
    Guid? CommentId = null,
    string? PostTitle = null);

/// <summary>
/// Sends in-app notifications (and emails, by each person's settings). Implemented by the Notifications module;
/// without it, nothing is sent. Never throws: a notification that can't be sent is logged, and the action that caused
/// it stands.
/// </summary>
public interface INotificationSender
{
    Task NotifyAsync(NotificationRequest request, CancellationToken cancellationToken);
}

/// <summary>Used when the Notifications module isn't hosted.</summary>
internal sealed class NoNotifications : INotificationSender
{
    public Task NotifyAsync(NotificationRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// For modules that send notifications: registers a sender that does nothing, unless the Notifications module
    /// has registered (or later registers) the real one.
    /// </summary>
    public static IServiceCollection AddNotificationSenderFallback(this IServiceCollection services)
    {
        services.TryAddScoped<INotificationSender, NoNotifications>();
        return services;
    }
}
