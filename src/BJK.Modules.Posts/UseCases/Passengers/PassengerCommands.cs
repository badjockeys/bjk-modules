using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Notifications;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Passengers;

/// <summary>Posts the signed-in user is tagged in as a passenger, with this status (Pending: waiting for them).</summary>
public sealed record ListTaggedPostsQuery(PassengerTagStatus Status = PassengerTagStatus.Pending, int Page = 1, int PageSize = 20)
    : IRequest<PagedResult<PostDto>>;

public sealed class ListTaggedPostsQueryValidator : AbstractValidator<ListTaggedPostsQuery>
{
    public ListTaggedPostsQueryValidator()
    {
        RuleFor(q => q.Status).IsInEnum();
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class ListTaggedPostsQueryHandler(IPostRepository posts, PostViews views, ICurrentUser caller)
    : IRequestHandler<ListTaggedPostsQuery, PagedResult<PostDto>>
{
    public async Task<PagedResult<PostDto>> Handle(ListTaggedPostsQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var (items, total) = await posts.ListTaggedAsync(userId, request.Status, request.Page, request.PageSize, cancellationToken);
        return new PagedResult<PostDto>(
            await views.BuildAsync(items, userId, cancellationToken),
            request.Page,
            request.PageSize,
            total);
    }
}

/// <summary>
/// The signed-in user accepts (their name then shows on the post) or declines a passenger tag. 404 if they aren't
/// tagged on it.
/// </summary>
public sealed record RespondToTagCommand(Guid PostId, bool Accept) : IRequest<PostDto>;

public sealed class RespondToTagCommandHandler(
    IPostRepository posts,
    PostViews views,
    ICurrentUser caller,
    TimeProvider clock,
    INotificationSender? notifications = null)
    : IRequestHandler<RespondToTagCommand, PostDto>
{
    public async Task<PostDto> Handle(RespondToTagCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var (post, tag) = await MyTag.GetAsync(posts, userId, request.PostId, cancellationToken);
        if (tag.Status == PassengerTagStatus.Declined && request.Accept)
        {
            throw PostAccess.Invalid("Accept", "You declined this tag. Ask the author to tag you again on a new post.");
        }

        if (tag.Respond(request.Accept, clock.GetUtcNow()))
        {
            await posts.SaveChangesAsync(cancellationToken);

            // The author hears about acceptances only (a decline just quietly removes the name).
            if (request.Accept && notifications is not null)
            {
                await notifications.NotifyAsync(
                    new NotificationRequest(post.AuthorId, NotificationType.PassengerAccepted, userId, post.Id, PostTitle: post.Title),
                    cancellationToken);
            }
        }

        return await views.BuildAsync(post, userId, cancellationToken);
    }
}

/// <summary>The signed-in user removes themselves from a post they were tagged on. 404 if they aren't tagged on it.</summary>
public sealed record RemoveMyTagCommand(Guid PostId) : IRequest;

public sealed class RemoveMyTagCommandHandler(IPostRepository posts, ICurrentUser caller)
    : IRequestHandler<RemoveMyTagCommand>
{
    public async Task Handle(RemoveMyTagCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var (_, tag) = await MyTag.GetAsync(posts, userId, request.PostId, cancellationToken);
        posts.RemovePassenger(tag);
        await posts.SaveChangesAsync(cancellationToken);
    }
}

internal static class MyTag
{
    public static async Task<(Post Post, PostPassenger Tag)> GetAsync(
        IPostRepository posts,
        Guid userId,
        Guid postId,
        CancellationToken cancellationToken)
    {
        var post = await posts.GetAsync(postId, cancellationToken);
        var tag = post is null || post.Status == PostStatus.Deleted
            ? null
            : (await posts.GetPassengersAsync([postId], cancellationToken)).FirstOrDefault(t => t.UserId == userId);
        return tag is null ? throw new NotFoundException("Passenger tag", postId) : (post!, tag);
    }
}
