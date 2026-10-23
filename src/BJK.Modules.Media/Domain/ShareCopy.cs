namespace BJK.Modules.Media.Domain;

/// <summary>Where a share copy is.</summary>
public enum ShareCopyStatus
{
    /// <summary>Queued for the worker, or being made.</summary>
    Rendering,

    /// <summary>Made, and kept privately (<see cref="ShareCopy.Key"/>) for its owner to download.</summary>
    Ready,

    /// <summary>The worker couldn't make it. Asking again starts over.</summary>
    Failed,
}

/// <summary>
/// A vertical copy of a posted video for TikTok, Instagram Reels, YouTube Shorts and X (owner, 23 Oct 2026): 1080×1920,
/// made from the already blurred, silent published file, with a small Bad Jockeys watermark and a short end card. Only
/// the uploader can ask for one or download it. Stored in <c>media.ShareCopies</c>, one row per media, separate from
/// the media row so the worker making it never clashes with other changes to the media.
/// </summary>
public sealed class ShareCopy
{
    /// <summary>A copy still "rendering" after this long is taken to have been lost, and can be asked for again.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>How long a made copy is offered for download before it's made afresh (the bucket keeps it 90 days).</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    // For EF Core.
    private ShareCopy()
    {
    }

    public Guid MediaId { get; private set; }

    /// <summary>The uploader: the only person who may download it.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The media revision it was made from. A re-processed video (a moderator added blur) needs a new copy.</summary>
    public int Revision { get; private set; }

    public ShareCopyStatus Status { get; private set; }

    /// <summary>The file in the private originals bucket once Ready (<see cref="MediaKeys.ShareVideo"/>).</summary>
    public string? Key { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? ReadyAt { get; private set; }

    /// <summary>Starts a copy of the media as it is now.</summary>
    /// <param name="media">A Ready video.</param>
    /// <param name="now">The time.</param>
    public static ShareCopy Start(MediaItem media, DateTimeOffset now) => new()
    {
        MediaId = media.Id,
        UserId = media.UserId,
        Revision = media.Revision,
        Status = ShareCopyStatus.Rendering,
        RequestedAt = now,
    };

    /// <summary>Starts again (failed, lost, out of date or expired): the same row, for the media's current revision.</summary>
    /// <param name="media">The media.</param>
    /// <param name="now">The time.</param>
    public void Restart(MediaItem media, DateTimeOffset now)
    {
        Revision = media.Revision;
        Status = ShareCopyStatus.Rendering;
        Key = null;
        ReadyAt = null;
        RequestedAt = now;
    }

    /// <param name="key">Where the worker stored it.</param>
    /// <param name="now">The time.</param>
    public void MarkReady(string key, DateTimeOffset now)
    {
        Status = ShareCopyStatus.Ready;
        Key = key;
        ReadyAt = now;
    }

    public void MarkFailed() => Status = ShareCopyStatus.Failed;

    /// <summary>True if this copy shows the media as it is now (made from its current revision).</summary>
    /// <param name="media">The media.</param>
    public bool IsCurrentFor(MediaItem media) => Revision == media.Revision;

    /// <summary>True while the worker is making it (and hasn't been at it so long it must have been lost).</summary>
    /// <param name="now">The time.</param>
    public bool IsRendering(DateTimeOffset now) => Status == ShareCopyStatus.Rendering && now - RequestedAt < StaleAfter;

    /// <summary>True if it's made and still offered for download.</summary>
    /// <param name="now">The time.</param>
    public bool IsAvailable(DateTimeOffset now) =>
        Status == ShareCopyStatus.Ready && Key is not null && ReadyAt is { } at && now - at < KeptFor;
}
