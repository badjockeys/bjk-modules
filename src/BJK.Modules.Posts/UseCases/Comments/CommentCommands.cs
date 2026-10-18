using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Safety;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Safety;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Comments;

/// <summary>
/// Adds a comment to a published post, or (with <c>ParentCommentId</c>) a reply to one of its top-level comments.
/// Replies go one level deep: replying to a reply is refused with a 400. The commenter needs a profile and a passed
/// age check, and can't comment on the posts of someone who has blocked them, or reply to someone who has. The text is
/// screened: high scores are held for review (only the commenter sees it), medium scores are published but flagged.
/// </summary>
public sealed record AddCommentCommand(Guid PostId, string Body, Guid? ParentCommentId = null)
    : IRequest<CommentDto>, IRequiresAgeVerification;

public sealed class AddCommentCommandValidator : AbstractValidator<AddCommentCommand>
{
    public AddCommentCommandValidator()
    {
        RuleFor(c => c.Body).CommentBody();
    }
}

public sealed class AddCommentCommandHandler(
    IPostRepository posts,
    ICommentRepository comments,
    IProfileDirectory profiles,
    IBlockDirectory blocks,
    IContentScreener screener,
    NewAccountLimits limits,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<AddCommentCommand, CommentDto>
{
    public async Task<CommentDto> Handle(AddCommentCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        PostAccess.RequireVerifiedEmail(caller, "commenting");
        var post = await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);
        await PostAccess.EnsureNotBlockedByAuthorAsync(blocks, post, userId, cancellationToken);

        var author = await profiles.GetSummariesAsync([userId], cancellationToken);
        if (author.Count == 0)
        {
            throw PostAccess.Invalid("Profile", "Create your profile before commenting.");
        }

        Comment? parent = null;
        if (request.ParentCommentId is { } parentId)
        {
            parent = await comments.GetAsync(post.Id, parentId, cancellationToken);
            if (parent is not { Status: CommentStatus.Visible, IsAuthorDeactivated: false })
            {
                throw new NotFoundException("Comment", parentId);
            }

            if (parent.IsReply)
            {
                throw PostAccess.Invalid(
                    nameof(request.ParentCommentId),
                    "You can't reply to a reply. Reply to the comment at the top of the thread instead.");
            }

            if (parent.AuthorId != userId && await blocks.IsBlockedAsync(parent.AuthorId, userId, cancellationToken))
            {
                throw new ForbiddenException("You can't reply to this comment.");
            }
        }

        await limits.EnsureCanCommentAsync(userId, author.GetValueOrDefault(userId), cancellationToken);

        var now = clock.GetUtcNow();
        var comment = parent is null
            ? Comment.Create(post.Id, userId, request.Body, now)
            : Comment.CreateReply(parent, userId, request.Body, now);
        comment.ApplyScreening(await screener.ScreenAsync(comment.Body, cancellationToken));
        comments.Add(comment);
        await comments.SaveChangesAsync(cancellationToken);

        return CommentViews.From(comment, author);
    }
}

/// <summary>Changes a comment's text. Only its author can. The new text is screened again.</summary>
public sealed record EditCommentCommand(Guid PostId, Guid CommentId, string Body) : IRequest<CommentDto>, IRequiresAgeVerification;

public sealed class EditCommentCommandValidator : AbstractValidator<EditCommentCommand>
{
    public EditCommentCommandValidator()
    {
        RuleFor(c => c.Body).CommentBody();
    }
}

public sealed class EditCommentCommandHandler(
    IPostRepository posts,
    ICommentRepository comments,
    IProfileDirectory profiles,
    IContentScreener screener,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<EditCommentCommand, CommentDto>
{
    public async Task<CommentDto> Handle(EditCommentCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);

        var comment = await comments.GetAsync(request.PostId, request.CommentId, cancellationToken);
        if (comment is not { Status: CommentStatus.Visible })
        {
            throw new NotFoundException("Comment", request.CommentId);
        }

        if (!comment.IsOwnedBy(userId))
        {
            throw new ForbiddenException("You can only edit your own comments.");
        }

        comment.Edit(request.Body, clock.GetUtcNow());
        comment.ApplyScreening(await screener.ScreenAsync(comment.Body, cancellationToken));
        await comments.SaveChangesAsync(cancellationToken);

        return CommentViews.From(comment, await profiles.GetSummariesAsync([userId], cancellationToken));
    }
}

/// <summary>
/// Deletes a comment. Its author (public endpoint) or a moderator (admin endpoint, <c>AsModerator</c>, with a
/// <c>Reason</c>, recorded in the moderation audit log).
/// </summary>
public sealed record DeleteCommentCommand(Guid PostId, Guid CommentId, bool AsModerator = false, string? Reason = null) : IRequest;

public sealed class DeleteCommentCommandValidator : AbstractValidator<DeleteCommentCommand>
{
    public DeleteCommentCommandValidator()
    {
        When(c => c.AsModerator, () => RuleFor(c => c.Reason).ModerationReason());
    }
}

public sealed class DeleteCommentCommandHandler(
    ICommentRepository comments,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<DeleteCommentCommand>
{
    public async Task Handle(DeleteCommentCommand request, CancellationToken cancellationToken)
    {
        if (request.AsModerator)
        {
            caller.RequireModerator(PostsModule.ManageScope);
        }

        var comment = await comments.GetAsync(request.PostId, request.CommentId, cancellationToken)
            ?? throw new NotFoundException("Comment", request.CommentId);

        var isAuthor = caller.UserId() is { } userId && comment.IsOwnedBy(userId);
        if (!isAuthor && !request.AsModerator)
        {
            throw new ForbiddenException("You can only delete your own comments.");
        }

        var before = comment.Status;
        comment.Delete(clock.GetUtcNow());
        if (request.AsModerator)
        {
            await audit.RecordAsync(
                PostsAudit.Entry(
                    ModerationActions.DeleteComment,
                    "Comment",
                    comment.Id,
                    request.Reason,
                    $"Status={before}",
                    $"Status={comment.Status}"),
                cancellationToken);
        }

        await comments.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// A post's top-level comments, oldest first, each with its reply count and first few replies. Signed-in users only:
/// logged-out viewers don't see comments or replies. Comments by people the viewer has blocked are left out (a
/// blocked user's top-level comment with other people's replies shows as "comment removed").
/// </summary>
public sealed record ListCommentsQuery(Guid PostId, int Page = 1, int PageSize = 50) : IRequest<PagedResult<CommentDto>>;

public sealed class ListCommentsQueryValidator : AbstractValidator<ListCommentsQuery>
{
    public ListCommentsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListCommentsQueryHandler(
    IPostRepository posts,
    ICommentRepository comments,
    CommentThreads threads,
    IBlockDirectory blocks,
    ICurrentUser caller)
    : IRequestHandler<ListCommentsQuery, PagedResult<CommentDto>>
{
    public async Task<PagedResult<CommentDto>> Handle(ListCommentsQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetVisibleAsync(posts, caller, request.PostId, cancellationToken);
        await PostAccess.EnsureAuthorNotBlockedByViewerAsync(blocks, post, caller, cancellationToken);

        var blocked = await blocks.GetBlockedIdsAsync(userId, cancellationToken);
        var (items, total) = await comments.ListTopLevelAsync(request.PostId, blocked, request.Page, request.PageSize, cancellationToken);

        return new PagedResult<CommentDto>(
            await threads.BuildTopLevelAsync(items, userId, blocked, cancellationToken),
            request.Page,
            request.PageSize,
            total);
    }
}

/// <summary>One page of a top-level comment's replies, oldest first. Signed-in users only.</summary>
public sealed record ListRepliesQuery(Guid PostId, Guid CommentId, int Page = 1, int PageSize = 50) : IRequest<PagedResult<CommentDto>>;

public sealed class ListRepliesQueryValidator : AbstractValidator<ListRepliesQuery>
{
    public ListRepliesQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListRepliesQueryHandler(
    IPostRepository posts,
    ICommentRepository comments,
    CommentThreads threads,
    IBlockDirectory blocks,
    ICurrentUser caller)
    : IRequestHandler<ListRepliesQuery, PagedResult<CommentDto>>
{
    public async Task<PagedResult<CommentDto>> Handle(ListRepliesQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetVisibleAsync(posts, caller, request.PostId, cancellationToken);
        await PostAccess.EnsureAuthorNotBlockedByViewerAsync(blocks, post, caller, cancellationToken);

        // The parent may be hidden or deleted (its replies still show); it just has to be a top-level comment here.
        var parent = await comments.FindAsync(request.CommentId, cancellationToken);
        if (parent is null || parent.PostId != post.Id || parent.IsReply)
        {
            throw new NotFoundException("Comment", request.CommentId);
        }

        var blocked = await blocks.GetBlockedIdsAsync(userId, cancellationToken);
        var (items, total) = await comments.ListRepliesPageAsync(parent.Id, blocked, request.Page, request.PageSize, cancellationToken);

        return new PagedResult<CommentDto>(
            await threads.BuildRepliesAsync(items, userId, cancellationToken),
            request.Page,
            request.PageSize,
            total);
    }
}

/// <summary>
/// Turns comments into <see cref="CommentDto"/>s with their authors, reaction counts, the viewer's reaction, reply
/// counts and the first <see cref="ReplyPreviewCount"/> replies, in a few batched queries.
/// </summary>
public sealed class CommentThreads(ICommentRepository comments, ICommentReactionRepository reactions, IProfileDirectory profiles)
{
    /// <summary>Replies included with each top-level comment in a list; the rest come from the replies endpoint.</summary>
    public const int ReplyPreviewCount = 3;

    // Caps the replies fetched for one page of comments.
    private const int MaxPreviewRows = 500;

    public async Task<IReadOnlyList<CommentDto>> BuildTopLevelAsync(
        IReadOnlyList<Comment> topLevel,
        Guid viewerId,
        IReadOnlyCollection<Guid> blocked,
        CancellationToken cancellationToken)
    {
        if (topLevel.Count == 0)
        {
            return [];
        }

        var parentIds = topLevel.Select(c => c.Id).ToList();
        var replyCounts = await comments.CountRepliesAsync(parentIds, blocked, cancellationToken);
        var replies = (await comments.ListRepliesAsync(parentIds, blocked, MaxPreviewRows, cancellationToken))
            .GroupBy(r => r.ParentCommentId!.Value)
            .ToDictionary(g => g.Key, g => g.Take(ReplyPreviewCount).ToList());

        var all = topLevel.Concat(replies.Values.SelectMany(r => r)).ToList();
        var authors = await profiles.GetSummariesAsync(all.Select(c => c.AuthorId).Distinct().ToList(), cancellationToken);
        var stats = await reactions.GetStatsAsync(all.Select(c => c.Id).ToList(), viewerId, cancellationToken);

        return topLevel.Select(c =>
        {
            var removed = c.Status != CommentStatus.Visible || c.IsAuthorDeactivated || blocked.Contains(c.AuthorId);
            var shownReplies = replies.TryGetValue(c.Id, out var found)
                ? found.Select(r => CommentViews.From(r, authors, stats.GetValueOrDefault(r.Id))).ToList()
                : new List<CommentDto>();
            return CommentViews.From(
                c,
                authors,
                stats.GetValueOrDefault(c.Id),
                replyCounts.GetValueOrDefault(c.Id),
                shownReplies,
                removed);
        }).ToList();
    }

    public async Task<IReadOnlyList<CommentDto>> BuildRepliesAsync(
        IReadOnlyList<Comment> items,
        Guid viewerId,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var authors = await profiles.GetSummariesAsync(items.Select(c => c.AuthorId).Distinct().ToList(), cancellationToken);
        var stats = await reactions.GetStatsAsync(items.Select(c => c.Id).ToList(), viewerId, cancellationToken);
        return items.Select(c => CommentViews.From(c, authors, stats.GetValueOrDefault(c.Id))).ToList();
    }
}

internal static class CommentViews
{
    public static CommentDto From(
        Comment comment,
        IReadOnlyDictionary<Guid, ProfileSummary> authors,
        CommentReactionStats? stats = null,
        int replyCount = 0,
        IReadOnlyList<CommentDto>? replies = null,
        bool removed = false)
    {
        var s = stats ?? CommentReactionStats.Empty;
        return new CommentDto(
            comment.Id,
            comment.PostId,
            removed ? null : AuthorDto.From(comment.AuthorId, authors),
            removed ? string.Empty : comment.Body,
            comment.CreatedAt,
            removed ? null : comment.UpdatedAt,
            removed ? CommentStatus.Visible : comment.Status,
            comment.ParentCommentId,
            removed,
            removed ? new Dictionary<ReactionType, int>() : s.Reactions,
            removed ? null : s.MyReaction,
            replyCount,
            replies);
    }

    public static IRuleBuilderOptions<T, string> CommentBody<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .NotEmpty()
            .Must(b => b is null || b.Trim().Length <= Comment.BodyMaxLength)
            .WithMessage($"Comments are {Comment.BodyMaxLength} characters or fewer.")
            .NoPersonalDetails();
}

/// <summary>Builds the Posts module's moderation audit entries.</summary>
internal static class PostsAudit
{
    public const string Module = "Posts";

    public static ModerationAuditEntry Entry(string action, string targetType, Guid targetId, string? reason, string? before, string? after) =>
        new(Module, action, targetType, targetId.ToString(), (reason ?? string.Empty).Trim(), before, after);
}
