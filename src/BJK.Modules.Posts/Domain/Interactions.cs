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

/// <summary>A comment on a post. Comments are flat (no replies), editable and deletable by their author.</summary>
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

    public string Body { get; private set; } = string.Empty;

    public CommentStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public string? HiddenReason { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>True while the author's account is deactivated: the comment is hidden but kept.</summary>
    public bool IsAuthorDeactivated { get; private set; }

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

    public bool IsOwnedBy(Guid userId) => AuthorId == userId;

    public void Edit(string body, DateTimeOffset now)
    {
        if (Status != CommentStatus.Visible)
        {
            throw new InvalidOperationException($"The comment is {Status}.");
        }

        Body = CheckedBody(body);
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

/// <summary>A user's report of a post or comment, for an admin to review. One per user per post or comment.</summary>
public sealed class Report
{
    public const int DetailsMaxLength = 1000;
    public const int ResolutionNoteMaxLength = 500;

    // For EF Core.
    private Report()
    {
    }

    public Guid Id { get; private set; }

    public Guid ReporterId { get; private set; }

    public ReportTargetType TargetType { get; private set; }

    /// <summary>The post or comment id.</summary>
    public Guid TargetId { get; private set; }

    /// <summary>The post concerned, also for comment reports, so admins can see the context.</summary>
    public Guid PostId { get; private set; }

    public ReportReason Reason { get; private set; }

    public string? Details { get; private set; }

    public ReportStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>The admin's user id, when the admin used a user token.</summary>
    public Guid? ResolvedBy { get; private set; }

    public string? ResolutionNote { get; private set; }

    public static Report Create(
        Guid reporterId,
        ReportTargetType targetType,
        Guid targetId,
        Guid postId,
        ReportReason reason,
        string? details,
        DateTimeOffset now)
    {
        var trimmed = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        if (trimmed is { Length: > DetailsMaxLength })
        {
            throw new ArgumentException($"Details are {DetailsMaxLength} characters or fewer.", nameof(details));
        }

        return new Report
        {
            Id = Guid.CreateVersion7(now),
            ReporterId = reporterId,
            TargetType = targetType,
            TargetId = targetId,
            PostId = postId,
            Reason = reason,
            Details = trimmed,
            Status = ReportStatus.Open,
            CreatedAt = now,
        };
    }

    public bool IsOpen => Status == ReportStatus.Open;

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
}
