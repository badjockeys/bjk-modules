namespace BJK.Modules.Media.Infrastructure.Messaging;

/// <summary>
/// The processing queue's rules. The queue is a table in the <c>messaging</c> schema (<c>[messaging].[media.process]</c>),
/// created by whichever host uses it first; jobs given up on go to <c>[messaging].[media.process.failed]</c>.
/// </summary>
/// <remarks>
/// A job a worker fails on, or crashes on, is tried again after a short delay; after <see cref="MaxDeliveries"/>
/// attempts the media is marked Failed and the job moves to the failed table.
/// </remarks>
public static class MediaQueueTopology
{
    public const int MaxDeliveries = 3;

    public static string FailedQueueName(string queueName) => queueName + ".failed";
}
