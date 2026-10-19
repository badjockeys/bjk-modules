using BJK.Modules.Media.Domain;

namespace BJK.Modules.Media.Contracts;

/// <summary>
/// What other modules (Profiles, Posts) may ask of the Media module. They never touch the media tables directly.
/// Callers do their own permission checks before calling <see cref="DeleteAsync"/>.
/// </summary>
public interface IMediaDirectory
{
    /// <summary>The requested media that exist and aren't deleted. Missing ids are simply left out.</summary>
    Task<IReadOnlyList<MediaSummary>> GetAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);

    /// <summary>Deletes the files and marks the media Deleted. Ids that don't exist are ignored.</summary>
    Task DeleteAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);
}

/// <summary>A piece of media as other modules see it. The URLs are only set once it's Ready.</summary>
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
    string? FailureReason = null)
{
    public bool IsReady => Status == MediaStatus.Ready;

    /// <summary>Still being processed or waiting for a moderator: it may yet become Ready.</summary>
    public bool IsPending => Status is MediaStatus.Processing or MediaStatus.NeedsReview;
}
