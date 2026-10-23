using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Safety;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Communities;

/// <summary>
/// Share your own post into communities you're a member of (owner, 23 Oct 2026: offered when posting). The post stays
/// public everywhere; communities it's already in are left as they are. Returns the communities it's in now.
/// </summary>
public sealed record SharePostToCommunitiesCommand(Guid PostId, IReadOnlyList<Guid> CommunityIds) : IRequest<IReadOnlyList<Guid>>;

/// <summary>Take a post out of a community: whoever shared it, or the community's admins and moderators.</summary>
public sealed record UnsharePostCommand(string Slug, Guid PostId) : IRequest;

/// <summary>Start a discussion in a community you're a member of.</summary>
public sealed record StartCommunityDiscussionCommand(string Slug, string Title, string? Body) : IRequest<CommunityDiscussionDto>;

/// <summary>Reply to a discussion in a community you're a member of.</summary>
public sealed record ReplyToCommunityDiscussionCommand(string Slug, Guid DiscussionId, string Body) : IRequest<CommunityReplyDto>;

/// <summary>Remove a discussion: its author, or the community's admins and moderators.</summary>
public sealed record RemoveCommunityDiscussionCommand(string Slug, Guid DiscussionId) : IRequest;

/// <summary>Remove a reply: its author, or the community's admins and moderators.</summary>
public sealed record RemoveCommunityReplyCommand(string Slug, Guid DiscussionId, Guid ReplyId) : IRequest;

public sealed class SharePostToCommunitiesCommandValidator : AbstractValidator<SharePostToCommunitiesCommand>
{
    public SharePostToCommunitiesCommandValidator()
    {
        RuleFor(c => c.CommunityIds).NotNull().Must(ids => ids.Count is >= 1 and <= 10).WithMessage("Choose 1 to 10 communities.");
    }
}

public sealed class StartCommunityDiscussionCommandValidator : AbstractValidator<StartCommunityDiscussionCommand>
{
    public StartCommunityDiscussionCommandValidator()
    {
        RuleFor(c => c.Title).NotEmpty().WithMessage("Give the discussion a title.").MaximumLength(CommunityDiscussion.TitleMaxLength);
        RuleFor(c => c.Body).MaximumLength(CommunityDiscussion.BodyMaxLength);
    }
}

public sealed class ReplyToCommunityDiscussionCommandValidator : AbstractValidator<ReplyToCommunityDiscussionCommand>
{
    public ReplyToCommunityDiscussionCommandValidator()
    {
        RuleFor(c => c.Body).NotEmpty().WithMessage("Write a reply.").MaximumLength(CommunityReply.BodyMaxLength);
    }
}

public sealed class SharePostToCommunitiesCommandHandler(
    ICommunityRepository communities,
    IPostRepository posts,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<SharePostToCommunitiesCommand, IReadOnlyList<Guid>>
{
    public async Task<IReadOnlyList<Guid>> Handle(SharePostToCommunitiesCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await posts.GetAsync(request.PostId, cancellationToken);
        if (post is null || post.AuthorId != userId || post.Status != PostStatus.Published)
        {
            throw new NotFoundException("Post", request.PostId);
        }

        var now = clock.GetUtcNow();
        foreach (var communityId in request.CommunityIds.Distinct())
        {
            var community = await communities.GetAsync(communityId, cancellationToken);
            if (community is null || community.IsRemoved)
            {
                throw new NotFoundException("Community", communityId);
            }

            await CommunityAccess.RequireMemberAsync(communities, community, userId, cancellationToken);
            if (await communities.GetShareAsync(community.Id, post.Id, cancellationToken) is null)
            {
                communities.Add(CommunityPost.Share(community.Id, post.Id, userId, now));
            }
        }

        await communities.SaveChangesAsync(cancellationToken);
        return await communities.ListSharedIntoAsync(post.Id, cancellationToken);
    }
}

public sealed class UnsharePostCommandHandler(ICommunityRepository communities, ICurrentUser caller)
    : IRequestHandler<UnsharePostCommand>
{
    public async Task Handle(UnsharePostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var share = await communities.GetShareAsync(community.Id, request.PostId, cancellationToken)
            ?? throw new NotFoundException("Shared post", request.PostId);
        if (share.SharedBy != userId)
        {
            await CommunityAccess.RequireModeratorAsync(communities, community, userId, cancellationToken);
        }

        communities.Remove(share);
        await communities.SaveChangesAsync(cancellationToken);
    }
}

public sealed class StartCommunityDiscussionCommandHandler(
    ICommunityRepository communities,
    IProfileDirectory profiles,
    IContentScreener screener,
    NewAccountLimits limits,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<StartCommunityDiscussionCommand, CommunityDiscussionDto>
{
    public async Task<CommunityDiscussionDto> Handle(StartCommunityDiscussionCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        PostAccess.RequireVerifiedEmail(caller, "starting a discussion");
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        await CommunityAccess.RequireMemberAsync(communities, community, userId, cancellationToken);
        var people = await profiles.GetSummariesAsync([userId], cancellationToken);
        await limits.EnsureCanCommentAsync(userId, people.GetValueOrDefault(userId), cancellationToken);
        var flagged = await CommunityRequests.ScreenAsync(screener, $"{request.Title}\n{request.Body}", cancellationToken);

        var discussion = CommunityDiscussion.Start(community.Id, userId, request.Title, request.Body, flagged, clock.GetUtcNow());
        communities.Add(discussion);
        await communities.SaveChangesAsync(cancellationToken);
        return CommunityViews.Discussion(discussion, people, canRemove: true);
    }
}

public sealed class ReplyToCommunityDiscussionCommandHandler(
    ICommunityRepository communities,
    IProfileDirectory profiles,
    IBlockDirectory blocks,
    IContentScreener screener,
    NewAccountLimits limits,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ReplyToCommunityDiscussionCommand, CommunityReplyDto>
{
    public async Task<CommunityReplyDto> Handle(ReplyToCommunityDiscussionCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        PostAccess.RequireVerifiedEmail(caller, "replying");
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        await CommunityAccess.RequireMemberAsync(communities, community, userId, cancellationToken);
        var discussion = await communities.GetDiscussionAsync(community.Id, request.DiscussionId, cancellationToken)
            ?? throw new NotFoundException("Discussion", request.DiscussionId);
        if (discussion.AuthorId != userId && await blocks.IsBlockedAsync(discussion.AuthorId, userId, cancellationToken))
        {
            throw new ForbiddenException("You can't reply to this discussion.");
        }

        var people = await profiles.GetSummariesAsync([userId], cancellationToken);
        await limits.EnsureCanCommentAsync(userId, people.GetValueOrDefault(userId), cancellationToken);
        var flagged = await CommunityRequests.ScreenAsync(screener, request.Body, cancellationToken);

        var now = clock.GetUtcNow();
        var reply = CommunityReply.Create(discussion, userId, request.Body, flagged, now);
        discussion.Replied(now);
        communities.Add(reply);
        await communities.SaveChangesAsync(cancellationToken);
        return new CommunityReplyDto(reply.Id, reply.Body, AuthorDto.From(userId, people), reply.CreatedAt, CanRemove: true);
    }
}

public sealed class RemoveCommunityDiscussionCommandHandler(ICommunityRepository communities, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<RemoveCommunityDiscussionCommand>
{
    public async Task Handle(RemoveCommunityDiscussionCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var discussion = await communities.GetDiscussionAsync(community.Id, request.DiscussionId, cancellationToken)
            ?? throw new NotFoundException("Discussion", request.DiscussionId);
        if (discussion.AuthorId != userId)
        {
            await CommunityAccess.RequireModeratorAsync(communities, community, userId, cancellationToken);
        }

        discussion.Remove(clock.GetUtcNow());
        await communities.SaveChangesAsync(cancellationToken);
    }
}

public sealed class RemoveCommunityReplyCommandHandler(ICommunityRepository communities, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<RemoveCommunityReplyCommand>
{
    public async Task Handle(RemoveCommunityReplyCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var discussion = await communities.GetDiscussionAsync(community.Id, request.DiscussionId, cancellationToken)
            ?? throw new NotFoundException("Discussion", request.DiscussionId);
        var reply = await communities.FindReplyAsync(request.ReplyId, cancellationToken);
        if (reply is null || reply.DiscussionId != discussion.Id || reply.Status != CommunityContentStatus.Visible)
        {
            throw new NotFoundException("Reply", request.ReplyId);
        }

        if (reply.AuthorId != userId)
        {
            await CommunityAccess.RequireModeratorAsync(communities, community, userId, cancellationToken);
        }

        reply.Remove(clock.GetUtcNow());
        discussion.ReplyRemoved();
        await communities.SaveChangesAsync(cancellationToken);
    }
}
