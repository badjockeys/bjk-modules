using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Comments;

/// <summary>A comment on someone's profile Replies tab, with the post it's on.</summary>
/// <param name="Comment">The comment or reply.</param>
/// <param name="Post">The post it's on.</param>
/// <param name="ReplyingTo">For a reply: the @handle of the comment it answers (null if that person's profile is gone).</param>
public sealed record ProfileReplyDto(CommentDto Comment, ReplyPostDto Post, string? ReplyingTo);

/// <summary>The post a profile reply is on.</summary>
/// <param name="Id">The post's id.</param>
/// <param name="Title">Its title.</param>
/// <param name="Author">Who posted it.</param>
public sealed record ReplyPostDto(Guid Id, string Title, AuthorDto Author);

/// <summary>
/// One person's comments and replies, newest first, for the Replies tab on their profile. Signed-in users only (like
/// comments). Only comments on posts everyone can see; people who have blocked each other see nothing, and posts by
/// people the viewer blocked are left out. People see their own held or hidden comments too.
/// </summary>
/// <param name="Handle">The profile's @handle.</param>
/// <param name="Page">Page number, from 1.</param>
/// <param name="PageSize">Comments per page, up to 50.</param>
public sealed record ListProfileRepliesQuery(string Handle, int Page = 1, int PageSize = 20)
    : IRequest<PagedResult<ProfileReplyDto>>;

public sealed class ListProfileRepliesQueryValidator : AbstractValidator<ListProfileRepliesQuery>
{
    public ListProfileRepliesQueryValidator()
    {
        RuleFor(q => q.Handle).NotEmpty().MaximumLength(30);
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class ListProfileRepliesQueryHandler(
    ICommentRepository comments,
    IPostRepository posts,
    CommentThreads threads,
    IProfileDirectory profiles,
    IBlockDirectory blocks,
    ICurrentUser caller)
    : IRequestHandler<ListProfileRepliesQuery, PagedResult<ProfileReplyDto>>
{
    public async Task<PagedResult<ProfileReplyDto>> Handle(ListProfileRepliesQuery request, CancellationToken cancellationToken)
    {
        var viewerId = caller.RequireUserId();
        var empty = new PagedResult<ProfileReplyDto>([], request.Page, request.PageSize, 0);
        var person = await profiles.FindByHandleAsync(request.Handle.Trim().TrimStart('@'), cancellationToken);
        if (person is null)
        {
            return empty;
        }

        var blockedEitherWay = await blocks.GetBlockedEitherWayAsync(viewerId, cancellationToken);
        if (blockedEitherWay.Contains(person.UserId))
        {
            return empty;
        }

        var blocked = await blocks.GetBlockedIdsAsync(viewerId, cancellationToken);
        var (items, total) = await comments.ListByAuthorAsync(
            person.UserId,
            blocked,
            includeOwnHidden: person.UserId == viewerId,
            request.Page,
            request.PageSize,
            cancellationToken);
        if (items.Count == 0)
        {
            return new PagedResult<ProfileReplyDto>([], request.Page, request.PageSize, total);
        }

        var built = (await threads.BuildRepliesAsync(items, viewerId, cancellationToken)).ToDictionary(c => c.Id);

        var postsById = new Dictionary<Guid, Domain.Post>();
        foreach (var postId in items.Select(c => c.PostId).Distinct())
        {
            if (await posts.GetAsync(postId, cancellationToken) is { } post)
            {
                postsById[postId] = post;
            }
        }

        var parents = new Dictionary<Guid, Guid>();
        foreach (var parentId in items.Where(c => c.ParentCommentId is not null).Select(c => c.ParentCommentId!.Value).Distinct())
        {
            if (await comments.FindAsync(parentId, cancellationToken) is { } parent)
            {
                parents[parentId] = parent.AuthorId;
            }
        }

        var people = await profiles.GetSummariesAsync(
            postsById.Values.Select(p => p.AuthorId).Concat(parents.Values).Distinct().ToList(),
            cancellationToken);

        var result = items
            .Where(c => postsById.ContainsKey(c.PostId))
            .Select(c =>
            {
                var post = postsById[c.PostId];
                var replyingTo = c.ParentCommentId is { } p && parents.TryGetValue(p, out var parentAuthor)
                    && people.TryGetValue(parentAuthor, out var who)
                    ? who.Handle
                    : null;
                return new ProfileReplyDto(
                    built[c.Id],
                    new ReplyPostDto(post.Id, post.Title, AuthorDto.From(post.AuthorId, people)),
                    replyingTo);
            })
            .ToList();
        return new PagedResult<ProfileReplyDto>(result, request.Page, request.PageSize, total);
    }
}
