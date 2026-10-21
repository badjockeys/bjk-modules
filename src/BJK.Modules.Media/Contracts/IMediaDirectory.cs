using BJK.Modules.Media.Domain;

namespace BJK.Modules.Media.Contracts;

/// <summary>
/// What other modules (Profiles, Posts) may ask of the Media module. They never touch the media tables directly.
/// Callers do their own permission checks before calling <see cref="DeleteAsync"/>.
/// </summary>
public interface IMediaDirectory
{
    /// <summary>
    /// The requested media that exist and aren't deleted. Missing ids are simply left out. Restricted media (in a post
    /// tagged sensitive, files kept privately) has no playback or thumbnail address here, only its blurred
    /// <see cref="MediaSummary.PreviewUrl"/>; use <see cref="GetWithSensitiveLinksAsync"/> for callers allowed to see it.
    /// </summary>
    Task<IReadOnlyList<MediaSummary>> GetAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="GetAsync"/>, but restricted media get short-lived signed links to the real files
    /// (<c>Media:SensitiveLinkLifetime</c>, 10 minutes by default; see <see cref="MediaSummary.LinksExpireAt"/>). Only
    /// for callers allowed to see sensitive media: age-verified adults, the uploader and moderators. The caller checks.
    /// </summary>
    Task<IReadOnlyList<MediaSummary>> GetWithSensitiveLinksAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);

    /// <summary>
    /// Marks media sensitive (its post was tagged sensitive) or clears the mark. Ready media then has its real files
    /// moved to private storage, with a heavily blurred preview published instead, or published again, by a job on the
    /// media worker (so it takes a few seconds); media still processing is handled when it's ready. Safe to repeat: a
    /// repeat also queues the job again if the files don't match the mark yet. Ids that don't exist are ignored. The
    /// caller does its own permission checks. Clearing leaves marks made by automatic graphic content detection in
    /// place unless <paramref name="clearAutomatic"/> is true (a moderator's decision), so an author can't undo them.
    /// </summary>
    /// <param name="mediaIds">The media.</param>
    /// <param name="isSensitive">Mark or clear.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <param name="clearAutomatic">When clearing: also clear automatic marks (moderators only).</param>
    Task SetSensitiveAsync(IReadOnlyCollection<Guid> mediaIds, bool isSensitive, CancellationToken cancellationToken, bool clearAutomatic = false);

    /// <summary>
    /// The post using these media has been published: their uploaded originals aren't needed any more and are deleted
    /// now (Ready media only; anything else keeps its original until it's ready, or the 24-hour sweep). Safe to repeat.
    /// </summary>
    Task ReleaseOriginalsAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);

    /// <summary>
    /// The CLIP summaries (see <see cref="ClipEmbedding"/>) of the given media that belong to <paramref name="userId"/>,
    /// are Ready and have one, by media id. For type suggestions; the caller does its own checks.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, float[]>> GetClipSummariesAsync(Guid userId, IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);

    /// <summary>Deletes the files and marks the media Deleted. Ids that don't exist are ignored.</summary>
    Task DeleteAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);
}

/// <summary>
/// A piece of media as other modules see it. The URLs are only set once it's Ready. For restricted media the playback
/// and thumbnail URLs are null, or short-lived signed links from <see cref="IMediaDirectory.GetWithSensitiveLinksAsync"/>.
/// </summary>
/// <param name="Id">Media id.</param>
/// <param name="UserId">The uploader.</param>
/// <param name="MediaType">Video or Image.</param>
/// <param name="Status">Where it is in processing.</param>
/// <param name="PlaybackUrl">The published file, once Ready.</param>
/// <param name="ThumbnailUrl">Its thumbnail, once Ready.</param>
/// <param name="DurationSeconds">Video length.</param>
/// <param name="Width">Width as seen.</param>
/// <param name="Height">Height as seen.</param>
/// <param name="FailureReason">Why it failed (shown to the uploader), when Failed.</param>
/// <param name="PreviewUrl">The heavily blurred public preview, for sensitive media (null until it's made).</param>
/// <param name="IsRestricted">True if the real files are kept privately because the media is sensitive.</param>
/// <param name="LinksExpireAt">When signed playback and thumbnail links stop working; null for public addresses.</param>
/// <param name="IsAutoSensitive">
/// True if the media worker's graphic content detection marked it sensitive. The Posts module then tags its post
/// sensitive (source Automatic) and queues it for a moderator to confirm or undo.
/// </param>
/// <param name="SensitiveScore">The graphic content score from the latest processing (0 to 1), or null if not scored.</param>
/// <param name="FeedUrl">
/// Photos: the feed-size copy (1080 px), for feeds; <paramref name="PlaybackUrl"/> is the full image. Null for videos and
/// older photos. Like the other addresses, null or signed for restricted media.
/// </param>
public sealed record MediaSummary(
    Guid Id,
    Guid UserId,
    MediaType MediaType,
    MediaStatus Status,
    Uri? PlaybackUrl,
    Uri? ThumbnailUrl,
    decimal? DurationSeconds,
    int? Width,
    int? Height,
    string? FailureReason = null,
    Uri? PreviewUrl = null,
    bool IsRestricted = false,
    DateTimeOffset? LinksExpireAt = null,
    bool IsAutoSensitive = false,
    double? SensitiveScore = null,
    Uri? FeedUrl = null)
{
    public bool IsReady => Status == MediaStatus.Ready;

    /// <summary>Still being processed or waiting for a moderator: it may yet become Ready.</summary>
    public bool IsPending => Status is MediaStatus.Processing or MediaStatus.NeedsReview;
}
