using BJK.Modules.Shared.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

public static class UserLifecycleEvents
{
    /// <summary>The topic the identity server publishes user lifecycle events to.</summary>
    public const string Topic = "bjk.users";

    /// <summary>The message type of a <see cref="UserLifecycleEvent"/>.</summary>
    public const string MessageType = nameof(UserLifecycleEvent);

    /// <summary>Attempts before a failing event is moved to the <c>.failed</c> dead-letter table for someone to look at.</summary>
    public const int MaxDeliveries = 5;

    /// <summary>
    /// Registers a module's handler. The first call also starts reading the host's
    /// <see cref="MessagingOptions.UserEventsQueue"/> (subscribed to <see cref="Topic"/>), passing each event to every
    /// registered handler, one event at a time.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Its configuration (<c>Messaging</c>).</param>
    /// <param name="connectionStringName">The module's connection string (the queues' default database).</param>
    public static IServiceCollection AddUserLifecycleHandler<THandler>(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName)
        where THandler : class, IUserLifecycleHandler
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserLifecycleHandler, THandler>());
        var queue = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()?.UserEventsQueue
            ?? new MessagingOptions().UserEventsQueue;
        services.AddMessageConsumer<UserLifecycleConsumer>(
            configuration,
            connectionStringName,
            new MessageConsumerDefinition(queue) { MaxAttempts = MaxDeliveries, Topics = [Topic] });
        return services;
    }
}

/// <summary>
/// Runs every module's <see cref="IUserLifecycleHandler"/> for one event. If any handler fails, the event is retried
/// (after a growing delay); after <see cref="UserLifecycleEvents.MaxDeliveries"/> attempts it moves to the dead-letter
/// table. An unreadable message is dead-lettered at once.
/// </summary>
internal sealed class UserLifecycleConsumer(
    IEnumerable<IUserLifecycleHandler> handlers,
    ILogger<UserLifecycleConsumer> logger)
    : IMessageConsumer
{
    public async Task<MessageOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        var userEvent = message.TryDeserialize<UserLifecycleEvent>();
        if (userEvent is null || userEvent.UserId == Guid.Empty)
        {
            logger.LogError("Unreadable user lifecycle message {MessageId}.", message.Id);
            return MessageOutcome.DeadLetter;
        }

        foreach (var handler in handlers)
        {
            await handler.HandleAsync(userEvent, cancellationToken);
        }

        logger.LogInformation("Handled {Type} for user {UserId} (attempt {Attempt}).", userEvent.Type, userEvent.UserId, message.Attempt);
        return MessageOutcome.Completed;
    }
}
