using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Storage;

namespace BJK.Modules.Media.UseCases;

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
    DateTimeOffset? ProcessedAt)
{
    internal static MediaDto From(MediaItem media, IMediaStorage storage) => new(
        media.Id,
        media.MediaType,
        media.Status,
        media.OriginalFileName,
        media.PlaybackKey is null ? null : storage.PublicUrl(media.PlaybackKey),
        media.ThumbnailKey is null ? null : storage.PublicUrl(media.ThumbnailKey),
        media.DurationSeconds,
        media.Width,
        media.Height,
        media.FailureReason,
        media.CreatedAt,
        media.UploadedAt,
        media.ProcessedAt);
}
