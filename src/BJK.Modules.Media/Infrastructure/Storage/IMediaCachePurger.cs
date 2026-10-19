using Microsoft.Extensions.Logging;

namespace BJK.Modules.Media.Infrastructure.Storage;

/// <summary>
/// Asks the CDN (Cloudflare) to forget public files that have just been taken down. Published files are cached for a
/// year, so after the real files of media in a post tagged sensitive are removed from the public bucket, Cloudflare can
/// keep serving its cached copies until they're purged. The default implementation only logs what needs purging;
/// a host can register a real one (e.g. Cloudflare's purge-by-URL API) before adding the Media module.
/// </summary>
public interface IMediaCachePurger
{
    /// <summary>Purges these public addresses from the CDN cache. Must be safe to repeat.</summary>
    /// <param name="mediaId">The media the files belong to (for logs).</param>
    /// <param name="urls">The public addresses that were taken down.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task PurgeAsync(Guid mediaId, IReadOnlyCollection<Uri> urls, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IMediaCachePurger"/>: logs a warning (event id <see cref="PurgeEventId"/>,
/// "MediaCachePurgeNeeded") naming the media and the files, without the full addresses (they contain the user id).
/// Until a real purger is registered, alert on that event and purge by hand, or rely on Cloudflare's cache rules.
/// </summary>
internal sealed class LoggingMediaCachePurger(ILogger<LoggingMediaCachePurger> logger) : IMediaCachePurger
{
    /// <summary>The log event id to alert on.</summary>
    public const int PurgeEventId = 4801;

    private static readonly Action<ILogger, Guid, int, string, Exception?> PurgeNeeded = LoggerMessage.Define<Guid, int, string>(
        LogLevel.Warning,
        new EventId(PurgeEventId, "MediaCachePurgeNeeded"),
        "Cloudflare cache purge needed for media {MediaId}: {Count} public file(s) taken down ({Files}). No purger is configured.");

    public Task PurgeAsync(Guid mediaId, IReadOnlyCollection<Uri> urls, CancellationToken cancellationToken)
    {
        if (urls.Count > 0)
        {
            var files = string.Join(", ", urls.Select(u => Path.GetFileName(u.AbsolutePath)));
            PurgeNeeded(logger, mediaId, urls.Count, files, null);
        }

        return Task.CompletedTask;
    }
}
