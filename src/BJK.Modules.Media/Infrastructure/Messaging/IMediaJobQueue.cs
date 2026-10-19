using System.Text.Json.Serialization;
using BJK.Modules.Media.Domain;

namespace BJK.Modules.Media.Infrastructure.Messaging;

/// <summary>Hands media to the processing workers. A small ticket goes on the queue; the file stays in storage.</summary>
public interface IMediaJobQueue
{
    Task EnqueueAsync(MediaProcessingJob job, CancellationToken cancellationToken);
}

/// <summary>The ticket a worker receives: which media to process and where its original is.</summary>
/// <param name="MediaId">The media.</param>
/// <param name="UserId">Its uploader.</param>
/// <param name="MediaType">Video or Image.</param>
/// <param name="OriginalKey">The original in the private bucket.</param>
/// <param name="ContentType">The original's type.</param>
public sealed record MediaProcessingJob(
    Guid MediaId,
    Guid UserId,
    MediaType MediaType,
    string OriginalKey,
    string ContentType)
{
    /// <summary>Which step: the whole media (the default, and how every job starts), one segment, or joining segments.</summary>
    public MediaJobStep Step { get; init; } = MediaJobStep.Process;

    /// <summary>For Segment and Assemble: the media revision the segments belong to. Stale jobs are skipped.</summary>
    public int Revision { get; init; }

    /// <summary>For Segment: which segment (from 0).</summary>
    public int? Segment { get; init; }

    /// <summary>A RabbitMQ message id unique to this step.</summary>
    [JsonIgnore]
    public string MessageId => Step switch
    {
        MediaJobStep.Segment => $"{MediaId:D}/r{Revision}/s{Segment}",
        MediaJobStep.Assemble => $"{MediaId:D}/r{Revision}/assemble",
        _ => MediaId.ToString("D"),
    };

    /// <summary>The job for one segment of this media.</summary>
    /// <param name="revision">The media revision.</param>
    /// <param name="segment">The segment.</param>
    public MediaProcessingJob ForSegment(int revision, int segment) =>
        this with { Step = MediaJobStep.Segment, Revision = revision, Segment = segment };

    /// <summary>The job that joins the segments of this media.</summary>
    /// <param name="revision">The media revision.</param>
    public MediaProcessingJob ForAssembly(int revision) =>
        this with { Step = MediaJobStep.Assemble, Revision = revision, Segment = null };
}

/// <summary>The steps of processing. Photos and short videos only ever need <see cref="Process"/>.</summary>
public enum MediaJobStep
{
    /// <summary>
    /// Download and check the original. Photos and short videos are processed there and then; longer videos are split
    /// into segments and a <see cref="Segment"/> job is queued for each.
    /// </summary>
    Process,

    /// <summary>Blur and encode one segment of a video, then queue <see cref="Assemble"/> if it was the last one left.</summary>
    Segment,

    /// <summary>Join the segments into the published video, make the thumbnail, and set Ready or NeedsReview.</summary>
    Assemble,
}
