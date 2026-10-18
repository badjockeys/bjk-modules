using RabbitMQ.Client;

namespace BJK.Modules.Media.Infrastructure.Messaging;

/// <summary>
/// Declares the processing queues. The API and the workers both call this, so the queues always
/// exist with the same settings, whichever starts first.
/// </summary>
/// <remarks>
/// The main queue is a quorum queue with a delivery limit: a job a worker rejects or crashes on is
/// redelivered, and after <see cref="MaxDeliveries"/> attempts RabbitMQ moves it to the failed queue.
/// </remarks>
public static class MediaQueueTopology
{
    public const int MaxDeliveries = 3;

    public static string FailedQueueName(string queueName) => queueName + ".failed";

    public static async Task DeclareAsync(IChannel channel, string queueName, CancellationToken cancellationToken)
    {
        var failedQueue = FailedQueueName(queueName);

        await channel.QueueDeclareAsync(
            queue: failedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: queueName,
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
    }
}
