using BJK.Modules.Media.Domain;

namespace BJK.Modules.Media.Infrastructure.Messaging;

/// <summary>Hands media to the processing workers. A small ticket goes on the queue; the file stays in storage.</summary>
public interface IMediaJobQueue
{
    Task EnqueueAsync(MediaProcessingJob job, CancellationToken cancellationToken);
}

/// <summary>The ticket a worker receives: which media to process and where its original is.</summary>
public sealed record MediaProcessingJob(
    Guid MediaId,
    Guid UserId,
    MediaType MediaType,
    string OriginalKey,
    string ContentType);
