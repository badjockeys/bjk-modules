using BJK.Modules.Shared.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Queues;

/// <summary>Reading user lifecycle events (published by the identity server's User module) off the queue.</summary>
public static class UserLifecycleQueueExtensions
{
    /// <summary>
    /// Registers a module's handler. The first call also starts reading the host's
    /// <see cref="MessagingOptions.UserEvents"/> (subscribed to <see cref="UserLifecycleEvents.Topic"/>), passing each
    /// event to every registered handler, one event at a time.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Its configuration (<c>Queues</c>).</param>
    /// <param name="connectionStringName">The module's connection string (the queues' default database).</param>
    public static IServiceCollection AddUserLifecycleHandler<THandler>(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName)
        where THandler : class, IUserLifecycleHandler
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserLifecycleHandler, THandler>());
        var queue = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()?.UserEvents
            ?? new MessagingOptions().UserEvents;
        services.AddMessageConsumer<UserLifecycleConsumer>(
            configuration,
            connectionStringName,
            new MessageConsumerDefinition(queue)
            {
                MaxAttempts = UserLifecycleEvents.MaxDeliveries,
                Topics = [UserLifecycleEvents.Topic],
            });
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
