using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Shared.Messaging;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>
/// Handles jobs from the <c>media.process</c> queue by running each through <see cref="MediaProcessor"/>. Several
/// workers can run side by side; each job is claimed by one of them (row locks on the queue table), so the segments of
/// one longer video are processed by several replicas at once. A job that keeps failing is tried
/// <see cref="MediaQueueTopology.MaxDeliveries"/> times; the last attempt marks the media Failed and the job moves to
/// the failed table, so nothing is left stuck at Processing. That includes a job whose worker crashed on every attempt:
/// the attempt count is kept with the message, and the next delivery after the last gives up straight away.
/// </summary>
internal sealed class MediaJobConsumer(MediaProcessor processor, ILogger<MediaJobConsumer> logger) : IMessageConsumer
{
    public async Task<MessageOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        var job = message.TryDeserialize<MediaProcessingJob>(MediaJobQueue.JsonOptions);
        if (job is null || job.MediaId == Guid.Empty)
        {
            logger.LogError("Unreadable media job {MessageId}.", message.Id);
            return MessageOutcome.DeadLetter;
        }

        var outcome = await processor.ProcessAsync(job, message.IsLastAttempt, cancellationToken, isRedelivery: message.IsRedelivery);
        return outcome switch
        {
            ProcessingOutcome.Retry => MessageOutcome.Retry,
            ProcessingOutcome.GaveUp => MessageOutcome.DeadLetter,
            _ => MessageOutcome.Completed,
        };
    }

    public async Task OnGivenUpAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        var job = message.TryDeserialize<MediaProcessingJob>(MediaJobQueue.JsonOptions);
        if (job is not null)
        {
            await processor.GiveUpAsync(job, cancellationToken);
        }
    }
}
