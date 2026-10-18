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
public sealed record MediaSummary(
    Guid Id,
    Guid UserId,
    MediaType MediaType,
    MediaStatus Status,
    Uri? PlaybackUrl,
    Uri? ThumbnailUrl,
    decimal? DurationSeconds,
    int? Width,
    int? Height)
{
    public bool IsReady => Status == MediaStatus.Ready;
}
