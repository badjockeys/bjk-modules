using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Comments;

/// <summary>Adds a comment to a published post. The commenter needs a profile.</summary>
public sealed record AddCommentCommand(Guid PostId, string Body) : IRequest<CommentDto>;

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
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<AddCommentCommand, CommentDto>
{
    public async Task<CommentDto> Handle(AddCommentCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);

        var author = await profiles.GetSummariesAsync([userId], cancellationToken);
        if (author.Count == 0)
        {
            throw PostAccess.Invalid("Profile", "Create your profile before commenting.");
        }

        var comment = Comment.Create(post.Id, userId, request.Body, clock.GetUtcNow());
        comments.Add(comment);
        await comments.SaveChangesAsync(cancellationToken);

        return CommentViews.From(comment, author);
    }
}

/// <summary>Changes a comment's text. Only its author can.</summary>
public sealed record EditCommentCommand(Guid PostId, Guid CommentId, string Body) : IRequest<CommentDto>;

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
        await comments.SaveChangesAsync(cancellationToken);

        return CommentViews.From(comment, await profiles.GetSummariesAsync([userId], cancellationToken));
    }
}

/// <summary>Deletes a comment. Its author or an admin.</summary>
public sealed record DeleteCommentCommand(Guid PostId, Guid CommentId) : IRequest;

public sealed class DeleteCommentCommandHandler(
    ICommentRepository comments,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<DeleteCommentCommand>
{
    public async Task Handle(DeleteCommentCommand request, CancellationToken cancellationToken)
    {
        var comment = await comments.GetAsync(request.PostId, request.CommentId, cancellationToken)
            ?? throw new NotFoundException("Comment", request.CommentId);

        var isAuthor = caller.UserId() is { } userId && comment.IsOwnedBy(userId);
        if (!isAuthor && !PostAccess.IsAdmin(caller))
        {
            throw new ForbiddenException("You can only delete your own comments.");
        }

        comment.Delete(clock.GetUtcNow());
        await comments.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>A post's comments, oldest first. Open to anonymous callers.</summary>
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
    ICurrentUser caller)
    : IRequestHandler<ListCommentsQuery, PagedResult<CommentDto>>
{
    public async Task<PagedResult<CommentDto>> Handle(ListCommentsQuery request, CancellationToken cancellationToken)
    {
        await PostAccess.GetVisibleAsync(posts, caller, request.PostId, cancellationToken);

        var (items, total) = await comments.ListVisibleAsync(request.PostId, request.Page, request.PageSize, cancellationToken);
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
        comment.UpdatedAt);

    public static IRuleBuilderOptions<T, string> CommentBody<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .NotEmpty()
            .Must(b => b is null || b.Trim().Length <= Comment.BodyMaxLength)
            .WithMessage($"Comments are {Comment.BodyMaxLength} characters or fewer.");
}
