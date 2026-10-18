using System.Text.Json;
using BJK.Modules.Shared.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

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
/// Published by the identity server (User module) to the <see cref="UserLifecycleEvents.Exchange"/> exchange
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
    /// <summary>Fanout exchange the identity server publishes user lifecycle events to.</summary>
    public const string Exchange = "bjk.users";

    /// <summary>Attempts before a failing event is moved to the <c>.failed</c> queue for someone to look at.</summary>
    public const int MaxDeliveries = 5;

    /// <summary>
    /// Registers a module's handler. The first call also starts the background consumer that reads the host's
    /// <see cref="MessagingOptions.UserEventsQueue"/> and passes each event to every registered handler.
    /// </summary>
    public static IServiceCollection AddUserLifecycleHandler<THandler>(
        this IServiceCollection services,
        IConfiguration configuration)
        where THandler : class, IUserLifecycleHandler
    {
        services.AddBjkMessaging(configuration);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserLifecycleHandler, THandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, UserLifecycleConsumer>());
        return services;
    }

    /// <summary>Declares the exchange, the host's queue (a quorum queue with a delivery limit) and its failed queue.</summary>
    public static async Task DeclareAsync(IChannel channel, string queue, CancellationToken cancellationToken)
    {
        var failedQueue = queue + ".failed";

        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queue: failedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queue: queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = MaxDeliveries,
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = failedQueue,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queue, Exchange, routingKey: string.Empty, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Reads user lifecycle events one at a time and runs every module's <see cref="IUserLifecycleHandler"/>.
/// If any handler fails, the event is returned to the queue and retried; after
/// <see cref="UserLifecycleEvents.MaxDeliveries"/> attempts it moves to the failed queue.
/// </summary>
internal sealed class UserLifecycleConsumer(
    IRabbitMqConnection rabbit,
    IServiceScopeFactory scopes,
    IOptions<MessagingOptions> options,
    ILogger<UserLifecycleConsumer> logger)
    : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = options.Value.UserEventsQueue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await rabbit.GetAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await UserLifecycleEvents.DeclareAsync(channel, queue, stoppingToken);
                await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
                logger.LogInformation("Listening for user lifecycle events on '{Queue}'.", queue);

                // Stay here until the channel closes (broker restart, network) or the host stops.
                while (channel.IsOpen && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The user lifecycle consumer stopped; reconnecting in {Delay}.", ReconnectDelay);
            }

            await Task.Delay(ReconnectDelay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        UserLifecycleEvent? userEvent;
        try
        {
            userEvent = JsonSerializer.Deserialize<UserLifecycleEvent>(delivery.Body.Span, OutboxMessage.JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Discarding an unreadable user lifecycle message {MessageId}.", delivery.BasicProperties.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken);
            return;
        }

        if (userEvent is null)
        {
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            foreach (var handler in scope.ServiceProvider.GetServices<IUserLifecycleHandler>())
            {
                await handler.HandleAsync(userEvent, cancellationToken);
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
            logger.LogInformation("Handled {Type} for user {UserId}.", userEvent.Type, userEvent.UserId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Handling {Type} for user {UserId} failed; it will be retried.", userEvent.Type, userEvent.UserId);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken);
        }
    }
}
