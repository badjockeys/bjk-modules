using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Safety;
using BJK.Modules.Shared.Security;
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
    TimeProvider clock)
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
