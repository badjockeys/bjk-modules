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
/// Adds a comment to a published post. The commenter needs a profile and a passed age check, and can't comment on the
/// posts of someone who has blocked them. The comment is screened: high scores are held for review (only the
/// commenter sees it), medium scores are published but flagged.
/// </summary>
public sealed record AddCommentCommand(Guid PostId, string Body) : IRequest<CommentDto>, IRequiresAgeVerification;

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

        await limits.EnsureCanCommentAsync(userId, author.GetValueOrDefault(userId), cancellationToken);

        var comment = Comment.Create(post.Id, userId, request.Body, clock.GetUtcNow());
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
/// A post's comments, oldest first. Signed-in users only: logged-out viewers don't see comments. Comments by people
/// the viewer has blocked are left out.
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
    IProfileDirectory profiles,
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
        var (items, total) = await comments.ListVisibleAsync(request.PostId, blocked, request.Page, request.PageSize, cancellationToken);
        var authors = await profiles.GetSummariesAsync(items.Select(c => c.AuthorId).Distinct().ToList(), cancellationToken);

        return new PagedResult<CommentDto>(
            items.Select(c => CommentViews.From(c, authors)).ToList(),
            request.Page,
            request.PageSize,
            total);
    }
}

internal static class CommentViews
{
    public static CommentDto From(Comment comment, IReadOnlyDictionary<Guid, ProfileSummary> authors) => new(
        comment.Id,
        comment.PostId,
        AuthorDto.From(comment.AuthorId, authors),
        comment.Body,
        comment.CreatedAt,
        comment.UpdatedAt,
        comment.Status);

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
