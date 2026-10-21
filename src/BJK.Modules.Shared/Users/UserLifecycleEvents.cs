namespace BJK.Modules.Shared.Users;

public enum UserLifecycleEventType
{
    /// <summary>The user deactivated their account (or an admin did). Hide their content; keep it.</summary>
    Deactivated,

    /// <summary>The user came back within the grace period. Show their content again.</summary>
    Reactivated,

    /// <summary>The grace period ended (or an admin deleted the account). Permanently delete everything.</summary>
    Deleted,
}

/// <summary>
/// Published by the identity server (User module) to the <see cref="UserLifecycleEvents.Topic"/> topic
/// whenever an account changes state, so every host can hide, show or delete that user's data.
/// </summary>
public sealed record UserLifecycleEvent(Guid EventId, Guid UserId, UserLifecycleEventType Type, DateTimeOffset OccurredAt);

/// <summary>
/// A module's reaction to <see cref="UserLifecycleEvent"/>s. Must be idempotent: an event can arrive more than once.
/// </summary>
public interface IUserLifecycleHandler
{
    Task HandleAsync(UserLifecycleEvent userEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Constants for the user lifecycle events. Modules register their <see cref="IUserLifecycleHandler"/> with
/// <c>AddUserLifecycleHandler</c> from the BJK.Modules.Queues package, which reads the events off the queue.
/// </summary>
public static class UserLifecycleEvents
{
    /// <summary>The topic the identity server publishes user lifecycle events to.</summary>
    public const string Topic = "bjk.users";

    /// <summary>The message type of a <see cref="UserLifecycleEvent"/>.</summary>
    public const string MessageType = nameof(UserLifecycleEvent);

    /// <summary>Attempts before a failing event is moved to the <c>.failed</c> dead-letter table for someone to look at.</summary>
    public const int MaxDeliveries = 5;
}
