using System.Text.Json;
using System.Text.Json.Serialization;
using BJK.Modules.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Messaging;

/// <summary>
/// Puts processing jobs on the <c>media.process</c> queue (a SQL Server table) through the host's
/// <see cref="IMessageSender"/>. Inside <see cref="MessagingTransaction.SaveChangesWithMessagesAsync"/> the job is
/// written in the same transaction as the media change; elsewhere (workers queueing segment jobs) it's committed at once.
/// </summary>
internal sealed class MediaJobQueue(IMessageSender sender, IOptions<MediaQueueOptions> options) : IMediaJobQueue
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Task EnqueueAsync(MediaProcessingJob job, CancellationToken cancellationToken) =>
        sender.SendAsync(
            options.Value.QueueName,
            OutgoingMessage.Create(job.MessageId, nameof(MediaProcessingJob), job, JsonOptions),
            cancellationToken);
}
