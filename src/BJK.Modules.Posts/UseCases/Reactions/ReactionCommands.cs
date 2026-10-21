using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Notifications;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Reactions;

/// <summary>
/// Sets the signed-in user's reaction to a post, replacing any earlier one. Needs a passed age check; refused on the
/// posts of someone who has blocked the user.
/// </summary>
public sealed record ReactToPostCommand(Guid PostId, ReactionType Type) : IRequest, IRequiresAgeVerification;

public sealed class ReactToPostCommandValidator : AbstractValidator<ReactToPostCommand>
{
    public ReactToPostCommandValidator()
    {
        RuleFor(c => c.Type).IsInEnum();
    }
}

public sealed class ReactToPostCommandHandler(
    IPostRepository posts,
    IReactionRepository reactions,
    IBlockDirectory blocks,
    ICurrentUser caller,
    TimeProvider clock,
    INotificationSender? notifications = null)
    : IRequestHandler<ReactToPostCommand>
{
    public async Task Handle(ReactToPostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);
        await PostAccess.EnsureNotBlockedByAuthorAsync(blocks, post, userId, cancellationToken);

        var existing = await reactions.GetAsync(post.Id, userId, cancellationToken);
        if (existing is null)
        {
            reactions.Add(Reaction.Create(post.Id, userId, request.Type, clock.GetUtcNow()));
        }
        else if (existing.Type != request.Type)
        {
            existing.Change(request.Type, clock.GetUtcNow());
        }
        else
        {
            return;
        }

        await reactions.SaveChangesAsync(cancellationToken);

        // Only for a new reaction, not a change of mind. The Notifications module groups them per post.
        if (existing is null && notifications is not null)
        {
            await notifications.NotifyAsync(
                new NotificationRequest(post.AuthorId, NotificationType.ReactionOnPost, userId, post.Id, PostTitle: post.Title),
                cancellationToken);
        }
    }
}

/// <summary>Removes the signed-in user's reaction. Does nothing if they hadn't reacted.</summary>
public sealed record RemoveReactionCommand(Guid PostId) : IRequest;

public sealed class RemoveReactionCommandHandler(IReactionRepository reactions, ICurrentUser caller)
    : IRequestHandler<RemoveReactionCommand>
{
    public async Task Handle(RemoveReactionCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var existing = await reactions.GetAsync(request.PostId, userId, cancellationToken);
        if (existing is null)
        {
            return;
        }

        reactions.Remove(existing);
        await reactions.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Sets the signed-in user's reaction to a comment or reply (one per comment, replacing any earlier one). Needs a
/// passed age check; refused if the comment's author or the post's author has blocked the user.
/// </summary>
public sealed record ReactToCommentCommand(Guid PostId, Guid CommentId, ReactionType Type) : IRequest, IRequiresAgeVerification;

public sealed class ReactToCommentCommandValidator : AbstractValidator<ReactToCommentCommand>
{
    public ReactToCommentCommandValidator()
    {
        RuleFor(c => c.Type).IsInEnum();
    }
}

public sealed class ReactToCommentCommandHandler(
    IPostRepository posts,
    ICommentRepository comments,
    ICommentReactionRepository reactions,
    IBlockDirectory blocks,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ReactToCommentCommand>
{
    public async Task Handle(ReactToCommentCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);
        await PostAccess.EnsureNotBlockedByAuthorAsync(blocks, post, userId, cancellationToken);

        var comment = await comments.GetAsync(post.Id, request.CommentId, cancellationToken);
        if (comment is not { Status: CommentStatus.Visible, IsAuthorDeactivated: false })
        {
            throw new NotFoundException("Comment", request.CommentId);
        }

        if (comment.AuthorId != userId && await blocks.IsBlockedAsync(comment.AuthorId, userId, cancellationToken))
        {
            throw new ForbiddenException("You can't react to this comment.");
        }

        var existing = await reactions.GetAsync(comment.Id, userId, cancellationToken);
        if (existing is null)
        {
            reactions.Add(CommentReaction.Create(comment.Id, userId, request.Type, clock.GetUtcNow()));
        }
        else if (existing.Type != request.Type)
        {
            existing.Change(request.Type, clock.GetUtcNow());
        }
        else
        {
            return;
        }

        await reactions.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Removes the signed-in user's reaction to a comment. Does nothing if they hadn't reacted.</summary>
public sealed record RemoveCommentReactionCommand(Guid PostId, Guid CommentId) : IRequest;

public sealed class RemoveCommentReactionCommandHandler(ICommentReactionRepository reactions, ICurrentUser caller)
    : IRequestHandler<RemoveCommentReactionCommand>
{
    public async Task Handle(RemoveCommentReactionCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var existing = await reactions.GetAsync(request.CommentId, userId, cancellationToken);
        if (existing is null)
        {
            return;
        }

        reactions.Remove(existing);
        await reactions.SaveChangesAsync(cancellationToken);
    }
}
