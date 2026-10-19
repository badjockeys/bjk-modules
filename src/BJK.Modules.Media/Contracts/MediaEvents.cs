using System.Text.Json;
using System.Text.Json.Serialization;
using BJK.Modules.Shared.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BJK.Modules.Media.Contracts;

/// <summary>What happened to a piece of media.</summary>
public enum MediaEventType
{
    /// <summary><c>media.ready</c>: processed (and approved, if it needed review) and published. It can be shown.</summary>
    Ready,

    /// <summary>
    /// <c>media.failed</c>: it will never be published: processing failed, a moderator rejected it, or it was deleted
    /// before it was ready. <see cref="MediaEvent.Reason"/> says why, in words the uploader can read.
    /// </summary>
    Failed,
}

/// <summary>
/// An integration event from the Media module, published to the <see cref="MediaEvents.Exchange"/> fanout exchange
/// through the module's outbox (<c>media.Outbox</c>) in the same transaction as the status change. The message type
/// is <c>media.ready</c> or <c>media.failed</c>. A message can arrive more than once, and events about different media
/// in any order, so handlers re-read the current state rather than trusting the event alone.
/// </summary>
/// <param name="EventId">Unique per event; the RabbitMQ message id.</param>
/// <param name="MediaId">The media.</param>
/// <param name="UserId">Its uploader.</param>
/// <param name="Type">Ready or Failed.</param>
/// <param name="Reason">For Failed: why, for the uploader.</param>
/// <param name="OccurredAt">When the status changed.</param>
public sealed record MediaEvent(
    Guid EventId,
    Guid MediaId,
    Guid UserId,
    MediaEventType Type,
    string? Reason,
    DateTimeOffset OccurredAt)
{
    /// <summary>The RabbitMQ message type: <c>media.ready</c> or <c>media.failed</c>.</summary>
    [JsonIgnore]
    public string MessageType => MediaEvents.MessageTypeOf(Type);
}

/// <summary>
/// A module's reaction to <see cref="MediaEvent"/>s (the Posts module publishes posts whose media are all ready).
/// Must be idempotent: the same event can arrive more than once.
/// </summary>
public interface IMediaEventHandler
{
    Task HandleAsync(MediaEvent mediaEvent, CancellationToken cancellationToken);
}

public static class MediaEvents
{
    /// <summary>Fanout exchange the Media module publishes media events to.</summary>
    public const string Exchange = "bjk.media";

    public const string ReadyType = "media.ready";

    public const string FailedType = "media.failed";

    /// <summary>Attempts before a failing event is moved to the <c>.failed</c> queue for someone to look at.</summary>
    public const int MaxDeliveries = 5;

    public static string MessageTypeOf(MediaEventType type) => type == MediaEventType.Ready ? ReadyType : FailedType;

    /// <summary>
    /// Registers a module's handler. The first call also starts the background consumer that reads the host's
    /// <see cref="MessagingOptions.MediaEventsQueue"/> and passes each event to every registered handler.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">For the RabbitMQ connection (<c>Messaging</c>).</param>
    public static IServiceCollection AddMediaEventHandler<THandler>(this IServiceCollection services, IConfiguration configuration)
        where THandler : class, IMediaEventHandler
    {
        services.AddBjkMessaging(configuration);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMediaEventHandler, THandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MediaEventConsumer>());
        return services;
    }

    /// <summary>Declares the exchange, the host's queue (a quorum queue with a delivery limit) and its failed queue.</summary>
    /// <param name="channel">An open channel.</param>
    /// <param name="queue">The host's queue.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
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
/// Reads media events one at a time and runs every module's <see cref="IMediaEventHandler"/>. If any handler fails,
/// the event goes back on the queue and is retried; after <see cref="MediaEvents.MaxDeliveries"/> attempts it moves to
/// the failed queue.
/// </summary>
internal sealed class MediaEventConsumer(
    IRabbitMqConnection rabbit,
    IServiceScopeFactory scopes,
    IOptions<MessagingOptions> options,
    ILogger<MediaEventConsumer> logger)
    : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = options.Value.MediaEventsQueue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await rabbit.GetAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await MediaEvents.DeclareAsync(channel, queue, stoppingToken);
                await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
                logger.LogInformation("Listening for media events on '{Queue}'.", queue);

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
                logger.LogError(ex, "The media event consumer stopped; reconnecting in {Delay}.", ReconnectDelay);
            }

            await Task.Delay(ReconnectDelay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        MediaEvent? mediaEvent;
        try
        {
            mediaEvent = JsonSerializer.Deserialize<MediaEvent>(delivery.Body.Span, OutboxMessage.JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Discarding an unreadable media event {MessageId}.", delivery.BasicProperties.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken);
            return;
        }

        if (mediaEvent is null || mediaEvent.MediaId == Guid.Empty)
        {
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            foreach (var handler in scope.ServiceProvider.GetServices<IMediaEventHandler>())
            {
                await handler.HandleAsync(mediaEvent, cancellationToken);
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
            logger.LogInformation("Handled {Type} for media {MediaId}.", mediaEvent.MessageType, mediaEvent.MediaId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Handling {Type} for media {MediaId} failed; it will be retried.", mediaEvent.MessageType, mediaEvent.MediaId);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken);
        }
    }
}
