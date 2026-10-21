using System.Text.Json.Serialization;
using BJK.Modules.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
/// An integration event from the Media module, published to the <see cref="MediaEvents.Topic"/> topic
/// through the module's outbox (<c>media.Outbox</c>) in the same transaction as the status change. The message type
/// is <c>media.ready</c> or <c>media.failed</c>. A message can arrive more than once, and events about different media
/// in any order, so handlers re-read the current state rather than trusting the event alone.
/// </summary>
/// <param name="EventId">Unique per event; the message id.</param>
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
    /// <summary>The message type: <c>media.ready</c> or <c>media.failed</c>.</summary>
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
    /// <summary>The topic the Media module publishes media events to (every subscribed queue gets a copy).</summary>
    public const string Topic = "bjk.media";

    public const string ReadyType = "media.ready";

    public const string FailedType = "media.failed";

    /// <summary>Attempts before a failing event is moved to the <c>.failed</c> dead-letter table for someone to look at.</summary>
    public const int MaxDeliveries = 5;

    public static string MessageTypeOf(MediaEventType type) => type == MediaEventType.Ready ? ReadyType : FailedType;

    /// <summary>
    /// Registers a module's handler. The first call also starts reading the host's
    /// <see cref="MessagingOptions.MediaEvents"/> (subscribed to <see cref="Topic"/>), passing each event to every
    /// registered handler.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Its configuration (<c>Queues</c>).</param>
    /// <param name="connectionStringName">The module's connection string (the queues' default database).</param>
    public static IServiceCollection AddMediaEventHandler<THandler>(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName)
        where THandler : class, IMediaEventHandler
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMediaEventHandler, THandler>());
        var queue = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()?.MediaEvents
            ?? new MessagingOptions().MediaEvents;
        services.AddMessageConsumer<MediaEventConsumer>(
            configuration,
            connectionStringName,
            new MessageConsumerDefinition(queue) { MaxAttempts = MaxDeliveries, Topics = [Topic] });
        return services;
    }
}

/// <summary>
/// Runs every module's <see cref="IMediaEventHandler"/> for one event. If any handler fails, the event is retried after
/// a growing delay; after <see cref="MediaEvents.MaxDeliveries"/> attempts it moves to the dead-letter table.
/// </summary>
internal sealed class MediaEventConsumer(
    IEnumerable<IMediaEventHandler> handlers,
    ILogger<MediaEventConsumer> logger)
    : IMessageConsumer
{
    public async Task<MessageOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        var mediaEvent = message.TryDeserialize<MediaEvent>();
        if (mediaEvent is null || mediaEvent.MediaId == Guid.Empty)
        {
            logger.LogError("Unreadable media event {MessageId}.", message.Id);
            return MessageOutcome.DeadLetter;
        }

        foreach (var handler in handlers)
        {
            await handler.HandleAsync(mediaEvent, cancellationToken);
        }

        logger.LogInformation("Handled {Type} for media {MediaId}.", mediaEvent.MessageType, mediaEvent.MediaId);
        return MessageOutcome.Completed;
    }
}
