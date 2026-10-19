using System.Text.Json;

namespace BJK.Modules.Shared.Messaging;

/// <summary>
/// A message to queue or publish: an id (consumers use it to spot repeats), a type name, and the content as JSON.
/// </summary>
/// <param name="Id">Unique per message, e.g. the event id, or a job id that is the same for repeats of the same job.</param>
/// <param name="Type">What it is, e.g. <c>media.ready</c> or <c>MediaProcessingJob</c>.</param>
/// <param name="Payload">The content as JSON.</param>
public sealed record OutgoingMessage(string Id, string Type, string Payload)
{
    /// <summary>Wait this long before any consumer can see the message (a delayed message). Null: straight away.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>A message with <paramref name="content"/> serialised as JSON.</summary>
    /// <param name="id">Unique per message.</param>
    /// <param name="type">The type name.</param>
    /// <param name="content">The content.</param>
    /// <param name="options">JSON options; <see cref="OutboxMessage.JsonOptions"/> if null.</param>
    public static OutgoingMessage Create<T>(string id, string type, T content, JsonSerializerOptions? options = null) =>
        new(id, type, JsonSerializer.Serialize(content, options ?? OutboxMessage.JsonOptions));
}

/// <summary>A message a consumer received, with which attempt this is.</summary>
/// <param name="Id">The message id.</param>
/// <param name="Type">The type name.</param>
/// <param name="Payload">The content as JSON.</param>
/// <param name="Attempt">1 the first time; counts every delivery, including ones a crashed or stopped worker never finished.</param>
/// <param name="MaxAttempts">After this many, a message that still fails goes to the queue's dead-letter table.</param>
public sealed record ReceivedMessage(string Id, string Type, string Payload, int Attempt, int MaxAttempts)
{
    /// <summary>If this attempt fails too, the message goes to the dead-letter table.</summary>
    public bool IsLastAttempt => Attempt >= MaxAttempts;

    /// <summary>An earlier delivery failed or never finished.</summary>
    public bool IsRedelivery => Attempt > 1;

    /// <summary>The content, or null if it isn't valid JSON for <typeparamref name="T"/>.</summary>
    /// <param name="options">JSON options; <see cref="OutboxMessage.JsonOptions"/> if null.</param>
    public T? TryDeserialize<T>(JsonSerializerOptions? options = null)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Payload, options ?? OutboxMessage.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What happens to a message after a consumer has seen it.</summary>
public enum MessageOutcome
{
    /// <summary>Done (or nothing to do): remove it from the queue.</summary>
    Completed,

    /// <summary>Something temporary went wrong: try again after a delay, or dead-letter it if that was the last attempt.</summary>
    Retry,

    /// <summary>It can never succeed (unreadable, or given up on): move it to the dead-letter table now.</summary>
    DeadLetter,
}

/// <summary>
/// Sends messages to durable queues. Messages sent inside
/// <see cref="MessagingTransaction.SaveChangesWithMessagesAsync"/> are written in the same database transaction as
/// the data change: both are saved, or neither.
/// </summary>
public interface IMessageSender
{
    /// <summary>Puts a message on one queue (point to point), e.g. a media processing job.</summary>
    /// <param name="queue">The queue, e.g. <c>media.process</c>.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task SendAsync(string queue, OutgoingMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Copies a message to every queue subscribed to <paramref name="topic"/> (publish/subscribe fan-out), e.g. user
    /// lifecycle events. Returns how many queues received it: 0 if nothing has subscribed yet.
    /// </summary>
    /// <param name="topic">The topic, e.g. <c>bjk.users</c>.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task<int> PublishAsync(string topic, OutgoingMessage message, CancellationToken cancellationToken);

    /// <summary>How many queues are subscribed to <paramref name="topic"/>.</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task<int> CountSubscribersAsync(string topic, CancellationToken cancellationToken);
}

/// <summary>
/// Handles the messages of one queue (see <see cref="MessagingServiceCollectionExtensions.AddMessageConsumer{TConsumer}"/>).
/// Resolved in a new scope for each message. Must be idempotent: a message can arrive more than once.
/// </summary>
public interface IMessageConsumer
{
    /// <summary>
    /// Handles one message. Throwing counts as <see cref="MessageOutcome.Retry"/>; stopping because the host is shutting
    /// down (cancellation) hands the message back without using up an attempt.
    /// </summary>
    /// <param name="message">The message and its attempt number.</param>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    Task<MessageOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Called once when a message is given up on: its last attempt failed, or the attempts ran out without a result (a
    /// worker crashed on it). The message then moves to the dead-letter table. Use it to record the failure, e.g. mark
    /// media Failed. Does nothing by default.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    Task OnGivenUpAsync(ReceivedMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>One queue a host reads, and how.</summary>
/// <param name="Queue">The queue name, e.g. <c>media.process</c>. Its dead-letter table is <c>{Queue}.failed</c>.</param>
public sealed record MessageConsumerDefinition(string Queue)
{
    /// <summary>Attempts before a message that keeps failing is dead-lettered.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Messages this host handles at once from this queue (each replica of the host).</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Topics whose messages are copied into this queue (publish/subscribe).</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];
}
