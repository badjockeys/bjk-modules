using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Security;

namespace BJK.Modules.Posts.UseCases;

/// <summary>
/// Who wrote a post or comment. <c>Handle</c> is null if the author's profile no longer exists. <c>RoadUser</c>
/// ("Driver", "Cyclist" or "Both") only if they chose to show it.
/// </summary>
public sealed record AuthorDto(Guid Id, string? Handle, string DisplayName, Uri? AvatarUrl, string? RoadUser = null)
{
    internal static AuthorDto From(Guid userId, IReadOnlyDictionary<Guid, ProfileSummary> profiles) =>
        profiles.TryGetValue(userId, out var p)
            ? new AuthorDto(p.UserId, p.Handle, p.DisplayName, p.AvatarUrl, p.RoadUser)
            : new AuthorDto(userId, null, "Former member", null);
}

/// <summary>
/// A passenger tagged on a post. Everyone sees Accepted ones ("with @sam_j"); the author also sees Pending ones, and
/// the tagged person sees their own tag whatever its status.
/// </summary>
public sealed record PassengerDto(AuthorDto Person, PassengerTagStatus Status);

/// <summary>Where it happened: the map pin, rounded to about 500 m when the post was saved, and the place name.</summary>
public sealed record LocationDto(decimal Latitude, decimal Longitude, string? PlaceName);

/// <summary>
/// A photo or video in a post. Media that isn't Ready (or was deleted) is left out. In a sensitive post seen by someone
/// who may not see it (logged out, or not age-verified), <c>IsBlurred</c> is true, <c>PlaybackUrl</c> and
/// <c>ThumbnailUrl</c> are null and <c>PreviewUrl</c> is a heavily blurred, low-resolution picture (null for the few
/// seconds until it's made: show a placeholder). Otherwise the real files: public addresses, or for sensitive media
/// short-lived signed links valid until <c>LinksExpireAt</c> (fetch the post again for fresh ones). Photos also have
/// <c>FeedUrl</c>, a 1080 px copy to show in feeds and lists; <c>PlaybackUrl</c> is the full 2048 px image, for when
/// it's tapped. <c>FeedUrl</c> is null for videos, photos processed before it existed, and blurred media.
/// </summary>
public sealed record PostMediaDto(
    Guid MediaId,
    MediaType MediaType,
    Uri? PlaybackUrl,
    Uri? ThumbnailUrl,
    decimal? DurationSeconds,
    int? Width,
    int? Height,
    Uri? PreviewUrl = null,
    bool IsBlurred = false,
    DateTimeOffset? LinksExpireAt = null,
    Uri? FeedUrl = null);

/// <summary>
/// A post. <c>IsSensitive</c> posts (graphic injuries, serious crashes) go to everyone, but unless the viewer is an
/// age-verified adult (token claim <c>age_verified</c> = "true"), the author or a moderator, their media come blurred
/// and <c>RequiresAgeVerification</c> is true: show a "Sensitive" label over the blurred previews and offer the age
/// check (or sign-in first, for logged-out viewers). <c>SensitiveSource</c> says who tagged it (Author, Moderator or
/// Automatic detection). <c>CommentsRequireSignIn</c> is true for
/// logged-out viewers: comments aren't shown to them (the count still is), so the app shows "Sign in to see comments".
/// <c>Status</c> is HeldForReview or Hidden only for the author (and moderators). <c>MediaState</c> is Pending while
/// the photos or video are still processing (show a "processing" badge; <c>Media</c> lists only the ready ones) and
/// Failed, with <c>MediaFailureReason</c>, if the post can't be published; others only ever see Ready posts.
/// <c>Types</c> are every type of bad driving on the post, shown as tags, the main one (<c>Category</c>) first.
/// <c>Passengers</c> are the people tagged as being in the author's vehicle (see <see cref="PassengerDto"/>).
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
    bool CommentsRequireSignIn = false,
    PostMediaState MediaState = PostMediaState.Ready,
    string? MediaFailureReason = null,
    bool RequiresAgeVerification = false,
    PostSensitiveSource SensitiveSource = PostSensitiveSource.None,
    IReadOnlyList<IncidentCategory>? Types = null,
    IReadOnlyList<PassengerDto>? Passengers = null)
{
    /// <summary>True while the post waits for its photos or video: the app shows a "processing" indicator.</summary>
    public bool IsProcessing => MediaState == PostMediaState.Pending;
}

/// <summary>
/// A comment or reply. <c>Status</c> is HeldForReview only for its author, while a moderator reviews it.
/// <c>IsRemoved</c> marks a top-level comment that was hidden or deleted but still has visible replies: it's shown as
/// a "comment removed" placeholder, with no author and an empty body. Top-level comments in a list carry their
/// <c>ReplyCount</c> and the first few <c>Replies</c>; the rest come from the replies endpoint.
/// </summary>
public sealed record CommentDto(
    Guid Id,
    Guid PostId,
    AuthorDto? Author,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    CommentStatus Status = CommentStatus.Visible,
    Guid? ParentCommentId = null,
    bool IsRemoved = false,
    IReadOnlyDictionary<ReactionType, int>? Reactions = null,
    ReactionType? MyReaction = null,
    int ReplyCount = 0,
    IReadOnlyList<CommentDto>? Replies = null);

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

/// <summary>
/// A post in the sensitive-review queue: tagged sensitive automatically, waiting for a moderator to confirm or undo it.
/// <c>Score</c> is the highest graphic content score of its media. <c>Post</c> carries the real media (moderators see it).
/// </summary>
public sealed record SensitiveReviewDto(
    PostDto Post,
    double? Score,
    SensitiveReviewStatus Review,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedBy);

/// <summary>A comment in a content screening queue, with its score and labels.</summary>
public sealed record ScreenedCommentDto(CommentDto Comment, bool IsFlagged, double? ScreeningScore, string? ScreeningLabels);

/// <summary>
/// Turns posts into <see cref="PostDto"/>s by gathering their authors (Profiles module), media (Media module)
/// and counts in a few batched calls, rather than one per post. Media of sensitive posts is blurred unless the caller
/// may see it: an age-verified adult, the author, or a moderator.
/// </summary>
public sealed class PostViews(IPostRepository posts, IProfileDirectory profiles, IMediaDirectory media, ICurrentUser caller)
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

        var tags = (await posts.GetPassengersAsync(items.Select(p => p.Id).ToList(), cancellationToken) ?? [])
            .ToLookup(t => t.PostId);
        var authors = await profiles.GetSummariesAsync(
            items.Select(p => p.AuthorId).Concat(tags.SelectMany(g => g.Select(t => t.UserId))).Distinct().ToList(),
            cancellationToken);
        var mediaById = (await media.GetAsync(
                items.SelectMany(p => p.Media).Select(m => m.MediaId).ToList(),
                cancellationToken))
            .ToDictionary(m => m.Id);

        // Real files of restricted (sensitive) media only for posts this caller may see unblurred: signed links.
        var blurred = items.Where(p => p.IsSensitive && !PostAccess.MayViewSensitiveMedia(p, caller, viewerId)).Select(p => p.Id).ToHashSet();
        var toSign = items
            .Where(p => !blurred.Contains(p.Id))
            .SelectMany(p => p.Media)
            .Select(m => m.MediaId)
            .Where(id => mediaById.TryGetValue(id, out var m) && m.IsRestricted)
            .ToList();
        if (toSign.Count > 0)
        {
            foreach (var signed in await media.GetWithSensitiveLinksAsync(toSign, cancellationToken))
            {
                mediaById[signed.Id] = signed;
            }
        }

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
                MediaFor(p, mediaById, blurred.Contains(p.Id)),
                s.Reactions,
                s.MyReaction,
                s.CommentCount,
                p.CreatedAt,
                p.UpdatedAt,
                p.IsSensitive,
                CommentsRequireSignIn: viewerId is null,
                MediaState: p.MediaState,
                MediaFailureReason: p.MediaFailureReason,
                RequiresAgeVerification: blurred.Contains(p.Id),
                SensitiveSource: p.SensitiveSource,
                Types: p.Types,
                Passengers: PassengersFor(p, tags[p.Id], authors, viewerId));
        }).ToList();
    }

    /// <summary>Accepted passengers for everyone; the author also sees Pending; you always see your own tag.</summary>
    private static List<PassengerDto> PassengersFor(
        Post post,
        IEnumerable<PostPassenger> tags,
        IReadOnlyDictionary<Guid, ProfileSummary> people,
        Guid? viewerId) =>
        tags
            .Where(t => people.ContainsKey(t.UserId))
            .Where(t => t.Status == PassengerTagStatus.Accepted
                || t.UserId == viewerId
                || (t.Status == PassengerTagStatus.Pending && post.AuthorId == viewerId))
            .OrderBy(t => t.TaggedAt)
            .Select(t => new PassengerDto(AuthorDto.From(t.UserId, people), t.Status))
            .ToList();

    /// <summary>
    /// The post's ready media, in order. Blurred: only the blurred preview, never a real address, whatever the Media
    /// module returned (its files may still be public for a few seconds after tagging).
    /// </summary>
    private static List<PostMediaDto> MediaFor(Post post, Dictionary<Guid, MediaSummary> mediaById, bool blurred) =>
        post.Media
            .OrderBy(m => m.Position)
            .Select(m => mediaById.GetValueOrDefault(m.MediaId))
            .OfType<MediaSummary>()
            .Where(m => m.IsReady && (blurred || m.PlaybackUrl is not null))
            .Select(m => blurred
                ? new PostMediaDto(m.Id, m.MediaType, null, null, m.DurationSeconds, m.Width, m.Height, m.PreviewUrl, IsBlurred: true)
                : new PostMediaDto(m.Id, m.MediaType, m.PlaybackUrl, m.ThumbnailUrl, m.DurationSeconds, m.Width, m.Height, m.PreviewUrl, IsBlurred: false, m.LinksExpireAt, m.FeedUrl))
            .ToList();
}
