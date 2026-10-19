using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Moderation;

/// <summary>Posts in a content screening queue (held or flagged), oldest first. Moderators and admins.</summary>
public sealed record ListScreenedPostsQuery(ScreeningQueue Queue = ScreeningQueue.Held, int Page = 1, int PageSize = 50)
    : IRequest<PagedResult<ScreenedPostDto>>;

/// <summary>Comments in a content screening queue (held or flagged), oldest first. Moderators and admins.</summary>
public sealed record ListScreenedCommentsQuery(ScreeningQueue Queue = ScreeningQueue.Held, int Page = 1, int PageSize = 50)
    : IRequest<PagedResult<ScreenedCommentDto>>;

public sealed class ListScreenedPostsQueryValidator : AbstractValidator<ListScreenedPostsQuery>
{
    public ListScreenedPostsQueryValidator()
    {
        RuleFor(q => q.Queue).IsInEnum();
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListScreenedCommentsQueryValidator : AbstractValidator<ListScreenedCommentsQuery>
{
    public ListScreenedCommentsQueryValidator()
    {
        RuleFor(q => q.Queue).IsInEnum();
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListScreenedPostsQueryHandler(IPostRepository posts, PostViews views, ICurrentUser caller)
    : IRequestHandler<ListScreenedPostsQuery, PagedResult<ScreenedPostDto>>
{
    public async Task<PagedResult<ScreenedPostDto>> Handle(ListScreenedPostsQuery request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(PostsModule.ManageScope);
        var (items, total) = await posts.ListForScreeningAsync(request.Queue, request.Page, request.PageSize, cancellationToken);
        var dtos = await views.BuildAsync(items, caller.UserId(), cancellationToken);
        return new PagedResult<ScreenedPostDto>(
            items.Zip(dtos, (p, dto) => new ScreenedPostDto(dto, p.IsFlagged, p.ScreeningScore, p.ScreeningLabels)).ToList(),
            request.Page,
            request.PageSize,
            total);
    }
}

public sealed class ListScreenedCommentsQueryHandler(ICommentRepository comments, IProfileDirectory profiles, ICurrentUser caller)
    : IRequestHandler<ListScreenedCommentsQuery, PagedResult<ScreenedCommentDto>>
{
    public async Task<PagedResult<ScreenedCommentDto>> Handle(ListScreenedCommentsQuery request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(PostsModule.ManageScope);
        var (items, total) = await comments.ListForScreeningAsync(request.Queue, request.Page, request.PageSize, cancellationToken);
        var authors = await profiles.GetSummariesAsync(items.Select(c => c.AuthorId).Distinct().ToList(), cancellationToken);
        return new PagedResult<ScreenedCommentDto>(
            items.Select(c => new ScreenedCommentDto(CommentViews.From(c, authors), c.IsFlagged, c.ScreeningScore, c.ScreeningLabels)).ToList(),
            request.Page,
            request.PageSize,
            total);
    }
}

/// <summary>What a moderator decides about screened content.</summary>
public enum ScreeningDecision
{
    /// <summary>Publish it (if held) and clear the flag.</summary>
    Approve,

    /// <summary>Hide it. The author can appeal.</summary>
    Remove,
}

/// <summary>A moderator's decision on a held or flagged post. A reason is required (audited).</summary>
public sealed record ReviewScreenedPostCommand(Guid PostId, ScreeningDecision Decision, string? Reason) : IRequest<ScreenedPostDto>;

/// <summary>A moderator's decision on a held or flagged comment. A reason is required (audited).</summary>
public sealed record ReviewScreenedCommentCommand(Guid CommentId, ScreeningDecision Decision, string? Reason) : IRequest<ScreenedCommentDto>;

public sealed class ReviewScreenedPostCommandValidator : AbstractValidator<ReviewScreenedPostCommand>
{
    public ReviewScreenedPostCommandValidator()
    {
        RuleFor(c => c.Decision).IsInEnum();
        RuleFor(c => c.Reason).ModerationReason();
    }
}

public sealed class ReviewScreenedCommentCommandValidator : AbstractValidator<ReviewScreenedCommentCommand>
{
    public ReviewScreenedCommentCommandValidator()
    {
        RuleFor(c => c.Decision).IsInEnum();
        RuleFor(c => c.Reason).ModerationReason();
    }
}

public sealed class ReviewScreenedPostCommandHandler(
    IPostRepository posts,
    PostViews views,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ReviewScreenedPostCommand, ScreenedPostDto>
{
    public async Task<ScreenedPostDto> Handle(ReviewScreenedPostCommand request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(PostsModule.ManageScope);
        var now = clock.GetUtcNow();
        var post = await posts.GetAsync(request.PostId, cancellationToken)
            ?? throw new NotFoundException("Post", request.PostId);

        var before = $"Status={post.Status}; Flagged={post.IsFlagged}";
        var reason = (request.Reason ?? string.Empty).Trim();
        if (request.Decision == ScreeningDecision.Approve)
        {
            post.Approve(caller.UserId(), now);
        }
        else
        {
            post.Hide(reason, now);
        }

        await audit.RecordAsync(
            PostsAudit.Entry(
                request.Decision == ScreeningDecision.Approve ? ModerationActions.ApprovePost : ModerationActions.HidePost,
                "Post",
                post.Id,
                reason,
                before,
                $"Status={post.Status}; Flagged={post.IsFlagged}"),
            cancellationToken);
        await posts.SaveChangesAsync(cancellationToken);

        var dto = await views.BuildAsync(post, caller.UserId(), cancellationToken);
        return new ScreenedPostDto(dto, post.IsFlagged, post.ScreeningScore, post.ScreeningLabels);
    }
}

public sealed class ReviewScreenedCommentCommandHandler(
    ICommentRepository comments,
    IProfileDirectory profiles,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ReviewScreenedCommentCommand, ScreenedCommentDto>
{
    public async Task<ScreenedCommentDto> Handle(ReviewScreenedCommentCommand request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(PostsModule.ManageScope);
        var now = clock.GetUtcNow();
        var comment = await comments.FindAsync(request.CommentId, cancellationToken)
            ?? throw new NotFoundException("Comment", request.CommentId);

        var before = $"Status={comment.Status}; Flagged={comment.IsFlagged}";
        var reason = (request.Reason ?? string.Empty).Trim();
        if (request.Decision == ScreeningDecision.Approve)
        {
            comment.Approve(caller.UserId(), now);
        }
        else
        {
            comment.Hide(reason, now);
        }

        await audit.RecordAsync(
            PostsAudit.Entry(
                request.Decision == ScreeningDecision.Approve ? ModerationActions.ApproveComment : ModerationActions.HideComment,
                "Comment",
                comment.Id,
                reason,
                before,
                $"Status={comment.Status}; Flagged={comment.IsFlagged}"),
            cancellationToken);
        await comments.SaveChangesAsync(cancellationToken);

        var authors = await profiles.GetSummariesAsync([comment.AuthorId], cancellationToken);
        return new ScreenedCommentDto(CommentViews.From(comment, authors), comment.IsFlagged, comment.ScreeningScore, comment.ScreeningLabels);
    }
}

/// <summary>A moderator sets or clears a post's sensitive tag. A reason is required (audited).</summary>
public sealed record SetPostSensitiveCommand(Guid PostId, bool IsSensitive, string? Reason) : IRequest<PostDto>;

public sealed class SetPostSensitiveCommandValidator : AbstractValidator<SetPostSensitiveCommand>
{
    public SetPostSensitiveCommandValidator()
    {
        RuleFor(c => c.Reason).ModerationReason();
    }
}

public sealed class SetPostSensitiveCommandHandler(
    IPostRepository posts,
    PostViews views,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<SetPostSensitiveCommand, PostDto>
{
    public async Task<PostDto> Handle(SetPostSensitiveCommand request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(PostsModule.ManageScope);
        var post = await posts.GetAsync(request.PostId, cancellationToken)
            ?? throw new NotFoundException("Post", request.PostId);

        var before = post.IsSensitive;
        post.SetSensitive(request.IsSensitive, clock.GetUtcNow());
        await audit.RecordAsync(
            PostsAudit.Entry(ModerationActions.SetPostSensitive, "Post", post.Id, request.Reason, $"Sensitive={before}", $"Sensitive={post.IsSensitive}"),
            cancellationToken);
        await posts.SaveChangesAsync(cancellationToken);

        return await views.BuildAsync(post, caller.UserId(), cancellationToken);
    }
}

/// <summary>
/// The moderation audit log, newest first, with optional filters. Admins only: it shows what every moderator did.
/// </summary>
public sealed record ListAuditLogQuery(
    Guid? ActorId = null,
    string? Module = null,
    string? Action = null,
    string? TargetType = null,
    string? TargetId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50) : IRequest<PagedResult<AuditEntryDto>>;

public sealed class ListAuditLogQueryValidator : AbstractValidator<ListAuditLogQuery>
{
    public ListAuditLogQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 200);
        RuleFor(q => q)
            .Must(q => q.From is null || q.To is null || q.From < q.To)
            .OverridePropertyName("From")
            .WithMessage("'from' must be before 'to'.");
    }
}

public sealed class ListAuditLogQueryHandler(IModerationAuditRepository records, ICurrentUser caller)
    : IRequestHandler<ListAuditLogQuery, PagedResult<AuditEntryDto>>
{
    public async Task<PagedResult<AuditEntryDto>> Handle(ListAuditLogQuery request, CancellationToken cancellationToken)
    {
        caller.RequireAdmin(PostsModule.ManageScope);
        var (items, total) = await records.SearchAsync(
            new AuditSearch(request.ActorId, request.Module, request.Action, request.TargetType, request.TargetId, request.From, request.To),
            request.Page,
            request.PageSize,
            cancellationToken);
        return new PagedResult<AuditEntryDto>(items.Select(AuditEntryDto.From).ToList(), request.Page, request.PageSize, total);
    }
}
