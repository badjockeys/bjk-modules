using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;

namespace BJK.Modules.Posts.UseCases;

/// <summary>Who wrote a post or comment. <c>Handle</c> is null if the author's profile no longer exists.</summary>
public sealed record AuthorDto(Guid Id, string? Handle, string DisplayName, Uri? AvatarUrl)
{
    internal static AuthorDto From(Guid userId, IReadOnlyDictionary<Guid, ProfileSummary> profiles) =>
        profiles.TryGetValue(userId, out var p)
            ? new AuthorDto(p.UserId, p.Handle, p.DisplayName, p.AvatarUrl)
            : new AuthorDto(userId, null, "Former member", null);
}

public sealed record LocationDto(decimal Latitude, decimal Longitude, string? PlaceName);

/// <summary>A photo or video in a post. Media that isn't Ready (or was deleted) is left out.</summary>
public sealed record PostMediaDto(
    Guid MediaId,
    MediaType MediaType,
    Uri PlaybackUrl,
    Uri? ThumbnailUrl,
    decimal? DurationSeconds,
    int? Width,
    int? Height);

public sealed record PostDto(
    Guid Id,
    AuthorDto Author,
    string Title,
    string? Description,
    IncidentCategory Category,
    DateTimeOffset IncidentAt,
    LocationDto Location,
    PostStatus Status,
    string? HiddenReason,
    IReadOnlyList<PostMediaDto> Media,
    IReadOnlyDictionary<ReactionType, int> Reactions,
    ReactionType? MyReaction,
    int CommentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record CommentDto(
    Guid Id,
    Guid PostId,
    AuthorDto Author,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record ReportDto(
    Guid Id,
    ReportTargetType TargetType,
    Guid TargetId,
    Guid PostId,
    ReportReason Reason,
    string? Details,
    ReportStatus Status,
    Guid ReporterId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedBy,
    string? ResolutionNote)
{
    internal static ReportDto From(Report r) => new(
        r.Id, r.TargetType, r.TargetId, r.PostId, r.Reason, r.Details, r.Status, r.ReporterId,
        r.CreatedAt, r.ResolvedAt, r.ResolvedBy, r.ResolutionNote);
}

/// <summary>
/// Turns posts into <see cref="PostDto"/>s by gathering their authors (Profiles module), media (Media module)
/// and counts in a few batched calls, rather than one per post.
/// </summary>
public sealed class PostViews(IPostRepository posts, IProfileDirectory profiles, IMediaDirectory media)
{
    public async Task<PostDto> BuildAsync(Post post, Guid? viewerId, CancellationToken cancellationToken) =>
        (await BuildAsync([post], viewerId, cancellationToken))[0];

    public async Task<IReadOnlyList<PostDto>> BuildAsync(
        IReadOnlyList<Post> items,
        Guid? viewerId,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var authors = await profiles.GetSummariesAsync(
            items.Select(p => p.AuthorId).Distinct().ToList(),
            cancellationToken);
        var mediaById = (await media.GetAsync(
                items.SelectMany(p => p.Media).Select(m => m.MediaId).ToList(),
                cancellationToken))
            .ToDictionary(m => m.Id);
        var stats = await posts.GetStatsAsync(items.Select(p => p.Id).ToList(), viewerId, cancellationToken);

        return items.Select(p =>
        {
            var s = stats.TryGetValue(p.Id, out var found) ? found : PostStats.Empty;
            return new PostDto(
                p.Id,
                AuthorDto.From(p.AuthorId, authors),
                p.Title,
                p.Description,
                p.Category,
                p.IncidentAt,
                new LocationDto(p.Latitude, p.Longitude, p.PlaceName),
                p.Status,
                p.HiddenReason,
                MediaFor(p, mediaById),
                s.Reactions,
                s.MyReaction,
                s.CommentCount,
                p.CreatedAt,
                p.UpdatedAt);
        }).ToList();
    }

    private static List<PostMediaDto> MediaFor(Post post, Dictionary<Guid, MediaSummary> mediaById) =>
        post.Media
            .OrderBy(m => m.Position)
            .Select(m => mediaById.GetValueOrDefault(m.MediaId))
            .OfType<MediaSummary>()
            .Where(m => m.IsReady && m.PlaybackUrl is not null)
            .Select(m => new PostMediaDto(m.Id, m.MediaType, m.PlaybackUrl!, m.ThumbnailUrl, m.DurationSeconds, m.Width, m.Height))
            .ToList();
}
