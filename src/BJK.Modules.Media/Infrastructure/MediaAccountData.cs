using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Media.Infrastructure;

/// <summary>
/// When an account is permanently deleted, removes every file in the user's storage folder and every media row.
/// Deactivation needs nothing here: the media is only reachable through the user's (now hidden) posts and profile.
/// </summary>
internal sealed class MediaUserLifecycleHandler(IMediaRepository media, IMediaStorage storage) : IUserLifecycleHandler
{
    public async Task HandleAsync(UserLifecycleEvent userEvent, CancellationToken cancellationToken)
    {
        if (userEvent.Type != UserLifecycleEventType.Deleted)
        {
            return;
        }

        // Files first: if that fails part-way, the rows still say what's left and the event is retried.
        await storage.DeleteAllForUserAsync(userEvent.UserId, cancellationToken);
        await media.DeleteAllForUserAsync(userEvent.UserId, cancellationToken);
    }
}

/// <summary>The user's uploads, with links to download each original (while it's kept) and processed file.</summary>
internal sealed class MediaPersonalDataExporter(MediaDbContext db, IMediaStorage storage) : IPersonalDataExporter
{
    private static readonly TimeSpan DownloadLinkLifetime = TimeSpan.FromHours(24);

    public string Section => "media";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var items = await db.Media.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        var result = new List<object>(items.Count);
        foreach (var m in items)
        {
            var hasOriginal = m.Status is not (MediaStatus.Deleted or MediaStatus.Uploading) && m.UploadedAt is not null;
            var links = m.Status == MediaStatus.Ready
                ? await MediaLinks.WithSensitiveAsync(storage, m, DownloadLinkLifetime, DateTimeOffset.UtcNow, cancellationToken)
                : MediaFileLinks.None;
            result.Add(new
            {
                m.Id,
                m.MediaType,
                m.Status,
                m.OriginalFileName,
                m.OriginalContentType,
                m.OriginalSizeBytes,
                m.DurationSeconds,
                m.Width,
                m.Height,
                m.CreatedAt,
                m.UploadedAt,
                m.ProcessedAt,
                m.DeletedAt,
                m.OriginalDeletesAt,
                OriginalDownloadUrl = hasOriginal
                    ? await storage.CreateOriginalDownloadUrlAsync(m.OriginalKey, DownloadLinkLifetime, cancellationToken)
                    : null,
                OriginalDownloadUrlExpiresIn = hasOriginal ? "24 hours" : null,
                // The user's own files: signed links (24 hours) if they're kept privately because the media is sensitive.
                PlaybackUrl = links.Playback,
                ThumbnailUrl = links.Thumbnail,
            });
        }

        return result;
    }
}
