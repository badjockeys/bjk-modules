using BJK.Modules.Shared.Safety;

namespace BJK.Modules.Posts.Domain;

/// <summary>One user's reaction to a post. Each user has at most one per post; reacting again changes it.</summary>
public sealed class Reaction
{
    // For EF Core.
    private Reaction()
    {
    }

    public Guid PostId { get; private set; }

    public Guid UserId { get; private set; }

    public ReactionType Type { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Reaction Create(Guid postId, Guid userId, ReactionType type, DateTimeOffset now) => new()
    {
        PostId = postId,
        UserId = userId,
        Type = type,
        CreatedAt = now,
    };

    public void Change(ReactionType type, DateTimeOffset now)
    {
        Type = type;
        CreatedAt = now;
    }
}

/// <summary>
/// One user's reaction to a comment or reply, in <c>posts.CommentReactions</c>. At most one per user per comment;
/// reacting again changes it. Deleted with the comment.
/// </summary>
public sealed class CommentReaction
{
    // For EF Core.
    private CommentReaction()
    {
    }

    public Guid CommentId { get; private set; }

    public Guid UserId { get; private set; }

    public ReactionType Type { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static CommentReaction Create(Guid commentId, Guid userId, ReactionType type, DateTimeOffset now) => new()
    {
        CommentId = commentId,
        UserId = userId,
        Type = type,
        CreatedAt = now,
    };

    public void Change(ReactionType type, DateTimeOffset now)
    {
        Type = type;
        CreatedAt = now;
    }
}

/// <summary>
/// A comment on a post, or a reply to one. Replies go one level deep only: a reply's <see cref="ParentCommentId"/>
/// is a top-level comment on the same post, and replies can't be replied to. Editable and deletable by the author;
/// every edit keeps the earlier text in <see cref="CommentEdit"/>. The post's author can hide it from everyone else.
/// </summary>
public sealed class Comment
{
    public const int BodyMaxLength = 2000;
    public const int HiddenReasonMaxLength = 500;

    // For EF Core.
    private Comment()
    {
    }

    public Guid Id { get; private set; }

    public Guid PostId { get; private set; }

    public Guid AuthorId { get; private set; }

    /// <summary>The top-level comment this replies to; null for a top-level comment.</summary>
    public Guid? ParentCommentId { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public CommentStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>When its author last changed the text; null if never. Earlier versions are in <see cref="CommentEdit"/>.</summary>
    public DateTimeOffset? EditedAt { get; private set; }

    /// <summary>
    /// The post's author hid it: everyone else sees a "Hidden by the post's author" placeholder (and, for a top-level
    /// comment, none of its replies). The commenter and the post's author still see it, marked hidden.
    /// </summary>
    public bool IsHiddenByPostAuthor { get; private set; }

    public DateTimeOffset? HiddenByPostAuthorAt { get; private set; }

    public string? HiddenReason { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>True while the author's account is deactivated: the comment is hidden but kept.</summary>
    public bool IsAuthorDeactivated { get; private set; }

    /// <summary>Content screening flagged the comment for a moderator to look at. Cleared when approved.</summary>
    public bool IsFlagged { get; private set; }

    /// <summary>The latest screening score, 0 to 1.</summary>
    public double? ScreeningScore { get; private set; }

    /// <summary>Why the text scored as it did, comma-separated (threat, insult...).</summary>
    public string? ScreeningLabels { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static Comment Create(Guid postId, Guid authorId, string body, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        PostId = postId,
        AuthorId = authorId,
        Body = CheckedBody(body),
        Status = CommentStatus.Visible,
        CreatedAt = now,
    };

    /// <summary>A reply to a top-level comment. Replies to replies aren't allowed.</summary>
    public static Comment CreateReply(Comment parent, Guid authorId, string body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.IsReply)
        {
            throw new InvalidOperationException("Replies can't be replied to: reply to the top-level comment instead.");
        }

        var reply = Create(parent.PostId, authorId, body, now);
        reply.ParentCommentId = parent.Id;
        return reply;
    }

    public bool IsReply => ParentCommentId is not null;

    public bool IsOwnedBy(Guid userId) => AuthorId == userId;

    /// <summary>
    /// Changes the text. Returns the earlier version to keep in the history, or null if the text is the same (nothing
    /// changes then).
    /// </summary>
    public CommentEdit? Edit(string body, DateTimeOffset now)
    {
        if (Status != CommentStatus.Visible)
        {
            throw new InvalidOperationException($"The comment is {Status}.");
        }

        var checkedBody = CheckedBody(body);
        if (checkedBody == Body)
        {
            return null;
        }

        var earlier = CommentEdit.Create(this, now);
        Body = checkedBody;
        EditedAt = now;
        UpdatedAt = now;
        return earlier;
    }

    /// <summary>The post's author hides the comment from everyone but themselves and the commenter.</summary>
    public void HideByPostAuthor(DateTimeOffset now)
    {
        if (IsHiddenByPostAuthor)
        {
            return;
        }

        IsHiddenByPostAuthor = true;
        HiddenByPostAuthorAt = now;
        UpdatedAt = now;
    }

    /// <summary>The post's author shows the comment again.</summary>
    public void ShowByPostAuthor(DateTimeOffset now)
    {
        if (!IsHiddenByPostAuthor)
        {
            return;
        }

        IsHiddenByPostAuthor = false;
        HiddenByPostAuthorAt = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Whether <paramref name="viewerId"/> sees the comment itself rather than the "Hidden by the post's author"
    /// placeholder: always, unless the post's author hid it and the viewer is neither them nor the commenter.
    /// </summary>
    public bool IsShownTo(Guid viewerId, Guid postAuthorId) =>
        !IsHiddenByPostAuthor || viewerId == AuthorId || viewerId == postAuthorId;

    /// <summary>
    /// Applies a content screening result. <see cref="ScreeningVerdict.Hold"/> holds a visible comment for review
    /// (only its author sees it); <see cref="ScreeningVerdict.Flag"/> flags it until a moderator approves it.
    /// </summary>
    public void ApplyScreening(ScreeningResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ScreeningScore = Math.Round(result.Score, 3);
        ScreeningLabels = ScreeningText.Labels(result.Labels);
        if (result.Verdict == ScreeningVerdict.Allow)
        {
            return;
        }

        IsFlagged = true;
        if (result.Verdict == ScreeningVerdict.Hold && Status == CommentStatus.Visible)
        {
            Status = CommentStatus.HeldForReview;
        }
    }

    /// <summary>A moderator approves the comment after screening: it becomes visible (if held) and the flag is cleared.</summary>
    public void Approve(Guid? moderatorId, DateTimeOffset now)
    {
        if (Status == CommentStatus.Deleted)
        {
            throw new InvalidOperationException("The comment has been deleted.");
        }

        if (Status == CommentStatus.HeldForReview)
        {
            Status = CommentStatus.Visible;
        }

        IsFlagged = false;
        ReviewedAt = now;
        ReviewedBy = moderatorId;
        UpdatedAt = now;
    }

    /// <summary>Makes a hidden comment visible again, e.g. after a successful appeal.</summary>
    public void Unhide(DateTimeOffset now)
    {
        if (Status != CommentStatus.Hidden)
        {
            return;
        }

        Status = CommentStatus.Visible;
        HiddenReason = null;
        UpdatedAt = now;
    }

    public void Hide(string reason, DateTimeOffset now)
    {
        if (Status == CommentStatus.Deleted)
        {
            return;
        }

        Status = CommentStatus.Hidden;
        HiddenReason = reason.Length <= HiddenReasonMaxLength ? reason : reason[..HiddenReasonMaxLength];
        UpdatedAt = now;
    }

    public void Delete(DateTimeOffset now)
    {
        if (Status == CommentStatus.Deleted)
        {
            return;
        }

        Status = CommentStatus.Deleted;
        DeletedAt = now;
        UpdatedAt = now;
    }

    private static string CheckedBody(string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        var trimmed = body.Trim();
        return trimmed.Length <= BodyMaxLength
            ? trimmed
            : throw new ArgumentException($"Comments are {BodyMaxLength} characters or fewer.", nameof(body));
    }
}

/// <summary>
/// An earlier version of a comment, kept when its author edits it. Anyone who can see the comment can see its
/// earlier versions ("Edited"). Deleted with the comment.
/// </summary>
public sealed class CommentEdit
{
    // For EF Core.
    private CommentEdit()
    {
    }

    public Guid Id { get; private set; }

    public Guid CommentId { get; private set; }

    /// <summary>The text as it was.</summary>
    public string Body { get; private set; } = string.Empty;

    /// <summary>When this version was written: the comment's creation or its previous edit.</summary>
    public DateTimeOffset WrittenAt { get; private set; }

    /// <summary>When it was replaced by the next version.</summary>
    public DateTimeOffset ReplacedAt { get; private set; }

    internal static CommentEdit Create(Comment comment, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        CommentId = comment.Id,
        Body = comment.Body,
        WrittenAt = comment.EditedAt ?? comment.CreatedAt,
        ReplacedAt = now,
    };
}


/// <summary>
/// A report of a post, comment or profile, for a moderator to review. From a signed-in user in the app (one per user
/// per item), or from anyone through the public "Report content / I'm in this clip" form. Illegal content and threats
/// are <see cref="ReportPriority.Urgent"/>.
/// </summary>
public sealed class Report
{
    public const int DetailsMaxLength = 1000;
    public const int ResolutionNoteMaxLength = 500;
    public const int ReporterNameMaxLength = 100;
    public const int ContactEmailMaxLength = 254;
    public const int TargetUrlMaxLength = 500;

    /// <summary>Target time for a decision on illegal content and threats.</summary>
    public static readonly TimeSpan UrgentResponseTime = TimeSpan.FromHours(4);

    /// <summary>Target time for a decision on everything else.</summary>
    public static readonly TimeSpan NormalResponseTime = TimeSpan.FromHours(48);

    // For EF Core.
    private Report()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The signed-in reporter. Null for reports through the public form.</summary>
    public Guid? ReporterId { get; private set; }

    public ReportSource Source { get; private set; }

    /// <summary>The name given on the public form.</summary>
    public string? ReporterName { get; private set; }

    /// <summary>
    /// Where the acknowledgement and outcome emails go: the address given on the public form, or the signed-in
    /// reporter's address from their token (if it carries one).
    /// </summary>
    public string? ContactEmail { get; private set; }

    public ReportTargetType TargetType { get; private set; }

    /// <summary>The post, comment or profile (user) id.</summary>
    public Guid TargetId { get; private set; }

    /// <summary>The post concerned, also for comment reports, so moderators can see the context. Null for profiles.</summary>
    public Guid? PostId { get; private set; }

    /// <summary>The link given on the public form, as entered.</summary>
    public string? TargetUrl { get; private set; }

    public ReportReason Reason { get; private set; }

    public string? Details { get; private set; }

    public ReportPriority Priority { get; private set; }

    /// <summary>When a decision is due: <see cref="UrgentResponseTime"/> or <see cref="NormalResponseTime"/> after the report.</summary>
    public DateTimeOffset DueAt { get; private set; }

    public ReportStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the acknowledgement email was sent.</summary>
    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>The moderator's user id, when they used a user token.</summary>
    public Guid? ResolvedBy { get; private set; }

    public string? ResolutionNote { get; private set; }

    /// <summary>When the outcome email was sent.</summary>
    public DateTimeOffset? OutcomeNotifiedAt { get; private set; }

    /// <summary>A short reference to quote in emails, e.g. R-1A2B3C4D.</summary>
    public string Reference => ShortReference.For("R", Id);

    public bool IsOpen => Status == ReportStatus.Open;

    public static ReportPriority PriorityFor(ReportReason reason) =>
        reason is ReportReason.IllegalContent or ReportReason.Threat ? ReportPriority.Urgent : ReportPriority.Normal;

    /// <summary>A signed-in user's report, from the app.</summary>
    public static Report Create(
        Guid reporterId,
        ReportTargetType targetType,
        Guid targetId,
        Guid? postId,
        ReportReason reason,
        string? details,
        DateTimeOffset now,
        string? contactEmail = null) =>
        New(reporterId, ReportSource.App, null, contactEmail, targetType, targetId, postId, null, reason, details, now);

    /// <summary>A report through the public form, from someone who may not have an account.</summary>
    public static Report CreatePublic(
        string reporterName,
        string contactEmail,
        ReportTargetType targetType,
        Guid targetId,
        Guid? postId,
        string? targetUrl,
        ReportReason reason,
        string? details,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reporterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contactEmail);
        return New(null, ReportSource.PublicForm, reporterName, contactEmail, targetType, targetId, postId, targetUrl, reason, details, now);
    }

    public void MarkAcknowledged(DateTimeOffset now) => AcknowledgedAt = now;

    public void MarkOutcomeNotified(DateTimeOffset now) => OutcomeNotifiedAt = now;

    public void Resolve(ReportAction action, Guid? resolvedBy, string? note, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("The report has already been resolved.");
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Status = action == ReportAction.HideContent ? ReportStatus.ContentHidden : ReportStatus.Dismissed;
        ResolvedBy = resolvedBy;
        ResolutionNote = trimmed is { Length: > ResolutionNoteMaxLength } ? trimmed[..ResolutionNoteMaxLength] : trimmed;
        ResolvedAt = now;
    }

    private static Report New(
        Guid? reporterId,
        ReportSource source,
        string? reporterName,
        string? contactEmail,
        ReportTargetType targetType,
        Guid targetId,
        Guid? postId,
        string? targetUrl,
        ReportReason reason,
        string? details,
        DateTimeOffset now)
    {
        var trimmed = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        if (trimmed is { Length: > DetailsMaxLength })
        {
            throw new ArgumentException($"Details are {DetailsMaxLength} characters or fewer.", nameof(details));
        }

        var priority = PriorityFor(reason);
        return new Report
        {
            Id = Guid.CreateVersion7(now),
            ReporterId = reporterId,
            Source = source,
            ReporterName = Limit(reporterName, ReporterNameMaxLength),
            ContactEmail = Limit(contactEmail, ContactEmailMaxLength),
            TargetType = targetType,
            TargetId = targetId,
            PostId = postId,
            TargetUrl = Limit(targetUrl, TargetUrlMaxLength),
            Reason = reason,
            Details = trimmed,
            Priority = priority,
            DueAt = now + (priority == ReportPriority.Urgent ? UrgentResponseTime : NormalResponseTime),
            Status = ReportStatus.Open,
            CreatedAt = now,
        };
    }

    private static string? Limit(string? value, int maxLength)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return trimmed is { Length: > 0 } && trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}

/// <summary>
/// A user's appeal against a moderation decision on their own post or comment (hidden after a report or by screening).
/// Each decision can be appealed once. Appeals go to the moderators' queue.
/// </summary>
public sealed class Appeal
{
    public const int StatementMaxLength = 1000;
    public const int DecisionNoteMaxLength = 500;

    /// <summary>Target time for a decision.</summary>
    public static readonly TimeSpan ResponseTime = TimeSpan.FromHours(48);

    // For EF Core.
    private Appeal()
    {
    }

    public Guid Id { get; private set; }

    public Guid AppellantId { get; private set; }

    /// <summary>Post or Comment.</summary>
    public ReportTargetType TargetType { get; private set; }

    public Guid TargetId { get; private set; }

    public Guid PostId { get; private set; }

    /// <summary>Why the user thinks the decision was wrong.</summary>
    public string Statement { get; private set; } = string.Empty;

    /// <summary>Where the decision email goes, from the user's token. Null if the token has no email claim.</summary>
    public string? ContactEmail { get; private set; }

    public AppealStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset DueAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public string? DecisionNote { get; private set; }

    /// <summary>A short reference to quote in emails, e.g. A-1A2B3C4D.</summary>
    public string Reference => ShortReference.For("A", Id);

    public bool IsOpen => Status == AppealStatus.Open;

    public static Appeal Create(
        Guid appellantId,
        ReportTargetType targetType,
        Guid targetId,
        Guid postId,
        string statement,
        string? contactEmail,
        DateTimeOffset now)
    {
        if (targetType is not (ReportTargetType.Post or ReportTargetType.Comment))
        {
            throw new ArgumentException("Only posts and comments can be appealed.", nameof(targetType));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        var trimmed = statement.Trim();
        if (trimmed.Length > StatementMaxLength)
        {
            throw new ArgumentException($"Appeals are {StatementMaxLength} characters or fewer.", nameof(statement));
        }

        return new Appeal
        {
            Id = Guid.CreateVersion7(now),
            AppellantId = appellantId,
            TargetType = targetType,
            TargetId = targetId,
            PostId = postId,
            Statement = trimmed,
            ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim(),
            Status = AppealStatus.Open,
            CreatedAt = now,
            DueAt = now + ResponseTime,
        };
    }

    public void Decide(AppealDecision decision, Guid? moderatorId, string note, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("The appeal has already been decided.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        var trimmed = note.Trim();
        Status = decision == AppealDecision.Overturn ? AppealStatus.Overturned : AppealStatus.Upheld;
        DecidedBy = moderatorId;
        DecisionNote = trimmed.Length <= DecisionNoteMaxLength ? trimmed : trimmed[..DecisionNoteMaxLength];
        DecidedAt = now;
    }
}

/// <summary>
/// One entry in the moderation audit log (<c>posts.ModerationAuditLog</c>): who did what to which item, why and when.
/// Written for every moderator or admin action in Posts, Profiles and Media. Never edited or deleted by the app.
/// </summary>
public sealed class ModerationAuditRecord
{
    public const int ActorRoleMaxLength = 20;
    public const int ModuleMaxLength = 20;
    public const int ActionMaxLength = 50;
    public const int TargetTypeMaxLength = 20;
    public const int TargetIdMaxLength = 100;
    public const int ReasonMaxLength = 500;
    public const int SummaryMaxLength = 500;

    // For EF Core.
    private ModerationAuditRecord()
    {
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>The moderator's or admin's user id; null for a machine token.</summary>
    public Guid? ActorId { get; private set; }

    /// <summary>Admin, Moderator or Service.</summary>
    public string ActorRole { get; private set; } = string.Empty;

    /// <summary>Posts, Profiles or Media.</summary>
    public string Module { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public string TargetType { get; private set; } = string.Empty;

    public string TargetId { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    /// <summary>A short summary of the state before, e.g. "Status=Published".</summary>
    public string? Before { get; private set; }

    /// <summary>A short summary of the state after, e.g. "Status=Hidden".</summary>
    public string? After { get; private set; }

    public static ModerationAuditRecord Create(
        Guid? actorId,
        string actorRole,
        string module,
        string action,
        string targetType,
        string targetId,
        string reason,
        string? before,
        string? after,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ModerationAuditRecord
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            ActorId = actorId,
            ActorRole = Cut(actorRole, ActorRoleMaxLength)!,
            Module = Cut(module, ModuleMaxLength)!,
            Action = Cut(action, ActionMaxLength)!,
            TargetType = Cut(targetType, TargetTypeMaxLength)!,
            TargetId = Cut(targetId, TargetIdMaxLength)!,
            Reason = Cut(reason.Trim(), ReasonMaxLength)!,
            Before = Cut(before, SummaryMaxLength),
            After = Cut(after, SummaryMaxLength),
        };
    }

    private static string? Cut(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}

internal static class ShortReference
{
    /// <summary>The prefix and the random last 8 hex digits of the id (the start of a version 7 GUID is a timestamp).</summary>
    public static string For(string prefix, Guid id) => $"{prefix}-{id.ToString("N")[^8..].ToUpperInvariant()}";
}
