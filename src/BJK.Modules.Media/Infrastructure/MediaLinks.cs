using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Storage;

namespace BJK.Modules.Media.Infrastructure;

/// <summary>Addresses of a media item's processed files.</summary>
/// <param name="Playback">The main file, or null.</param>
/// <param name="Thumbnail">The thumbnail, or null.</param>
/// <param name="ExpiresAt">When signed links stop working; null for public addresses.</param>
internal sealed record MediaFileLinks(Uri? Playback, Uri? Thumbnail, DateTimeOffset? ExpiresAt)
{
    public static readonly MediaFileLinks None = new(null, null, null);
}

/// <summary>
/// Works out the addresses to hand out for a media item's files: public addresses through Cloudflare, or, for
/// restricted (sensitive) media, nothing (<see cref="Public"/>) or short-lived signed links to the private copies
/// (<see cref="WithSensitiveAsync"/>, only for callers allowed to see sensitive media).
/// </summary>
internal static class MediaLinks
{
    /// <summary>Public addresses; none for restricted media, whose real files aren't public.</summary>
    public static MediaFileLinks Public(IMediaStorage storage, MediaItem item) =>
        item.IsRestricted
            ? MediaFileLinks.None
            : new MediaFileLinks(
                item.PlaybackKey is null ? null : storage.PublicUrl(item.PlaybackKey),
                item.ThumbnailKey is null ? null : storage.PublicUrl(item.ThumbnailKey),
                null);

    /// <summary>Public addresses, or signed links valid for <paramref name="lifetime"/> to restricted media's private copies.</summary>
    public static async Task<MediaFileLinks> WithSensitiveAsync(
        IMediaStorage storage,
        MediaItem item,
        TimeSpan lifetime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!item.IsRestricted)
        {
            return Public(storage, item);
        }

        var playback = item.PlaybackKey is null
            ? null
            : await storage.CreateDownloadUrlAsync(MediaBucket.Sensitive, MediaKeys.Sensitive(item.PlaybackKey), lifetime, cancellationToken);
        var thumbnail = item.ThumbnailKey is null
            ? null
            : await storage.CreateDownloadUrlAsync(MediaBucket.Sensitive, MediaKeys.Sensitive(item.ThumbnailKey), lifetime, cancellationToken);
        return new MediaFileLinks(playback, thumbnail, playback is null && thumbnail is null ? null : now + lifetime);
    }

    /// <summary>The blurred public preview of sensitive media, if it has one.</summary>
    public static Uri? Preview(IMediaStorage storage, MediaItem item) =>
        item.PreviewKey is null ? null : storage.PublicUrl(item.PreviewKey);
}
