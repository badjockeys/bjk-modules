using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure;
using BJK.Modules.Media.Infrastructure.Storage;

namespace BJK.Modules.Media.UseCases;

/// <summary>
/// A piece of media. <c>IsSensitive</c> media (in a post tagged sensitive) is shown blurred to anyone but the uploader,
/// moderators and age-verified adults: <c>IsBlurred</c> is then true, <c>PlaybackUrl</c> and <c>ThumbnailUrl</c> are
/// null and only <c>PreviewUrl</c> (a heavily blurred picture; null until it's made) is given. Callers who may see it
/// get short-lived signed links once its files are private, valid until <c>LinksExpireAt</c>. Photos have <c>FeedUrl</c>,
/// a 1080 px copy for feeds (<c>PlaybackUrl</c> is the full 2048 px image); null for videos and older photos.
/// </summary>
public sealed record MediaDto(
    Guid Id,
    MediaType MediaType,
    MediaStatus Status,
    string OriginalFileName,
    Uri? PlaybackUrl,
    Uri? ThumbnailUrl,
    decimal? DurationSeconds,
    int? Width,
    int? Height,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UploadedAt,
    DateTimeOffset? ProcessedAt,
    bool IsSensitive = false,
    Uri? PreviewUrl = null,
    bool IsBlurred = false,
    DateTimeOffset? LinksExpireAt = null,
    Uri? FeedUrl = null)
{
    /// <summary>Public addresses only (none for restricted media). For media that isn't Ready yet, e.g. just uploaded.</summary>
    internal static MediaDto From(MediaItem media, IMediaStorage storage) =>
        Build(media, MediaLinks.Public(storage, media), MediaLinks.Preview(storage, media), isSensitive: media.IsRestricted, blurred: false);

    /// <summary>
    /// The media as this caller may see it: blurred (preview only) if it's sensitive and <paramref name="mayView"/> is
    /// false; otherwise with real addresses, signed if its files are private.
    /// </summary>
    internal static async Task<MediaDto> ForViewerAsync(
        MediaItem media,
        IMediaStorage storage,
        bool isSensitive,
        bool mayView,
        TimeSpan linkLifetime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sensitive = isSensitive || media.IsRestricted;
        var preview = MediaLinks.Preview(storage, media);
        if (sensitive && !mayView)
        {
            return Build(media, MediaFileLinks.None, preview, isSensitive: true, blurred: true);
        }

        var links = await MediaLinks.WithSensitiveAsync(storage, media, linkLifetime, now, cancellationToken);
        return Build(media, links, preview, sensitive, blurred: false);
    }

    private static MediaDto Build(MediaItem media, MediaFileLinks links, Uri? preview, bool isSensitive, bool blurred) => new(
        media.Id,
        media.MediaType,
        media.Status,
        media.OriginalFileName,
        links.Playback,
        links.Thumbnail,
        media.DurationSeconds,
        media.Width,
        media.Height,
        media.FailureReason,
        media.CreatedAt,
        media.UploadedAt,
        media.ProcessedAt,
        isSensitive,
        preview,
        blurred,
        links.ExpiresAt,
        links.Feed);
}
