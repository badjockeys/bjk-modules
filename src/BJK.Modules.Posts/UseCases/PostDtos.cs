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

/// <summary>Where it happened: the map pin, rounded to about 500 m when the post was saved, and the place name.</summary>
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

/// <summary>
/// A post. <c>IsSensitive</c> posts are never sent to logged-out viewers. <c>CommentsRequireSignIn</c> is true for
/// logged-out viewers: comments aren't shown to them (the count still is), so the app shows "Sign in to see comments".
/// <c>Status</c> is HeldForReview or Hidden only for the author (and moderators).
/// </summary>
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
    DateTimeOffset? UpdatedAt,
    bool IsSensitive = false,
    bool CommentsRequireSignIn = false);

/// <summary>A comment. <c>Status</c> is HeldForReview only for its author, while a moderator reviews it.</summary>
public sealed record CommentDto(
    Guid Id,
    Guid PostId,
    AuthorDto Author,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    CommentStatus Status = CommentStatus.Visible);

/// <summary>A report, as moderators see it, including the public reporter's contact details.</summary>
public sealed record ReportDto(
    Guid Id,
    string Reference,
    ReportTargetType TargetType,
    Guid TargetId,
    Guid? PostId,
    string? TargetUrl,
    ReportReason Reason,
    string? Details,
    ReportPriority Priority,
    DateTimeOffset DueAt,
    ReportStatus Status,
    ReportSource Source,
    Guid? ReporterId,
    string? ReporterName,
    string? ContactEmail,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedBy,
    string? ResolutionNote,
    DateTimeOffset? OutcomeNotifiedAt)
{
    internal static ReportDto From(Report r) => new(
        r.Id, r.Reference, r.TargetType, r.TargetId, r.PostId, r.TargetUrl, r.Reason, r.Details, r.Priority, r.DueAt,
        r.Status, r.Source, r.ReporterId, r.ReporterName, r.ContactEmail, r.CreatedAt, r.AcknowledgedAt, r.ResolvedAt,
        r.ResolvedBy, r.ResolutionNote, r.OutcomeNotifiedAt);
}

/// <summary>
/// What someone gets back after reporting through the public form: a reference to quote, the priority, when a
/// decision is due and whether the acknowledgement email was sent.
/// </summary>
public sealed record ReportReceipt(string Reference, ReportPriority Priority, DateTimeOffset RespondBy, bool AcknowledgementSent)
{
    internal static ReportReceipt From(Report r, bool sent) => new(r.Reference, r.Priority, r.DueAt, sent);
}

/// <summary>An appeal against a moderation decision.</summary>
public sealed record AppealDto(
    Guid Id,
    string Reference,
    Guid AppellantId,
    ReportTargetType TargetType,
    Guid TargetId,
    Guid PostId,
    string Statement,
    AppealStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? DecidedAt,
    Guid? DecidedBy,
    string? DecisionNote)
{
    internal static AppealDto From(Appeal a) => new(
        a.Id, a.Reference, a.AppellantId, a.TargetType, a.TargetId, a.PostId, a.Statement, a.Status, a.CreatedAt,
        a.DueAt, a.DecidedAt, a.DecidedBy, a.DecisionNote);
}

/// <summary>One moderation audit log entry.</summary>
public sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorId,
    string ActorRole,
    string Module,
    string Action,
    string TargetType,
    string TargetId,
    string Reason,
    string? Before,
    string? After)
{
    internal static AuditEntryDto From(ModerationAuditRecord a) => new(
        a.Id, a.OccurredAt, a.ActorId, a.ActorRole, a.Module, a.Action, a.TargetType, a.TargetId, a.Reason, a.Before, a.After);
}

/// <summary>A post in a content screening queue, with its score and labels.</summary>
public sealed record ScreenedPostDto(PostDto Post, bool IsFlagged, double? ScreeningScore, string? ScreeningLabels);

/// <summary>A comment in a content screening queue, with its score and labels.</summary>
public sealed record ScreenedCommentDto(CommentDto Comment, bool IsFlagged, double? ScreeningScore, string? ScreeningLabels);

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
                p.UpdatedAt,
                p.IsSensitive,
                CommentsRequireSignIn: viewerId is null);
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
