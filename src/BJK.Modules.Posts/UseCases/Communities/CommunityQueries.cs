using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Communities;

/// <summary>
/// Communities to browse: <see cref="CommunityListKind.Near"/> (covering your home area), <see cref="CommunityListKind.Mine"/>,
/// <see cref="CommunityListKind.Topics"/> (no area) or <see cref="CommunityListKind.Search"/>. Most members first.
/// </summary>
public sealed record ListCommunitiesQuery(CommunityListKind Kind, string? Search = null, int Page = 1, int PageSize = 20)
    : IRequest<PagedResult<CommunitySummaryDto>>;

/// <summary>One community by its address, with what you may do in it. Removed ones are 404.</summary>
public sealed record GetCommunityQuery(string Slug) : IRequest<CommunityDto>;

/// <summary>Posts shared into a community, newest share first. Private ones: members only.</summary>
public sealed record ListCommunityPostsQuery(string Slug, int Page = 1, int PageSize = 20) : IRequest<PagedResult<PostDto>>;

/// <summary>A community's discussions, most recently active first. Private ones: members only.</summary>
public sealed record ListCommunityDiscussionsQuery(string Slug, int Page = 1, int PageSize = 20) : IRequest<PagedResult<CommunityDiscussionDto>>;

/// <summary>A discussion and a page of its replies.</summary>
public sealed record GetCommunityDiscussionQuery(string Slug, Guid DiscussionId, int Page = 1, int PageSize = 50) : IRequest<CommunityDiscussionDetailDto>;

/// <summary>
/// The people in a community. Members: anyone who can see inside. Requests, invitations and bans: its admins and
/// moderators.
/// </summary>
public sealed record ListCommunityMembersQuery(string Slug, CommunityMembershipStatus Status = CommunityMembershipStatus.Member, int Page = 1, int PageSize = 30)
    : IRequest<PagedResult<CommunityMemberDto>>;

/// <summary>
/// Your communities to share a post into (owner, 23 Oct 2026): those whose area covers where the incident happened
/// first, marked recommended. Optionally for an existing post (which marks the ones it's already in).
/// </summary>
public sealed record SuggestCommunitiesQuery(decimal? Latitude, decimal? Longitude, Guid? PostId = null) : IRequest<IReadOnlyList<CommunitySuggestionDto>>;

public sealed class ListCommunitiesQueryValidator : AbstractValidator<ListCommunitiesQuery>
{
    public ListCommunitiesQueryValidator()
    {
        RuleFor(q => q.Kind).IsInEnum();
        RuleFor(q => q.Search).NotEmpty().MaximumLength(60).When(q => q.Kind == CommunityListKind.Search);
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class CommunityPageValidators :
    AbstractValidator<ListCommunityPostsQuery>
{
    public CommunityPageValidators()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class ListCommunityDiscussionsQueryValidator : AbstractValidator<ListCommunityDiscussionsQuery>
{
    public ListCommunityDiscussionsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class GetCommunityDiscussionQueryValidator : AbstractValidator<GetCommunityDiscussionQuery>
{
    public GetCommunityDiscussionQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListCommunityMembersQueryValidator : AbstractValidator<ListCommunityMembersQuery>
{
    public ListCommunityMembersQueryValidator()
    {
        RuleFor(q => q.Status).IsInEnum();
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListCommunitiesQueryHandler(ICommunityRepository communities, IHomeAreas homeAreas, ICurrentUser caller)
    : IRequestHandler<ListCommunitiesQuery, PagedResult<CommunitySummaryDto>>
{
    public async Task<PagedResult<CommunitySummaryDto>> Handle(ListCommunitiesQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var query = new CommunityListQuery(request.Kind, userId, Search: request.Search);
        if (request.Kind == CommunityListKind.Near && await homeAreas.GetAsync(userId, cancellationToken) is { } home)
        {
            query = query with { Latitude = home.Latitude, Longitude = home.Longitude };
        }

        var (items, total) = await communities.ListAsync(query, request.Page, request.PageSize, cancellationToken);
        var mine = await communities.GetMembershipsAsync(userId, items.Select(c => c.Id).ToList(), cancellationToken);
        return new PagedResult<CommunitySummaryDto>(
            items.Select(c => CommunityAccess.Summary(c, mine.GetValueOrDefault(c.Id))).ToList(),
            request.Page,
            request.PageSize,
            total);
    }
}

public sealed class GetCommunityQueryHandler(ICommunityRepository communities, IProfileDirectory profiles, ICurrentUser caller)
    : IRequestHandler<GetCommunityQuery, CommunityDto>
{
    public async Task<CommunityDto> Handle(GetCommunityQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var member = await communities.GetMemberAsync(community.Id, userId, cancellationToken);
        var people = await profiles.GetSummariesAsync([community.OwnerId, userId], cancellationToken);
        var adult = people.TryGetValue(userId, out var me) && me.IsAdult;
        return CommunityAccess.Detail(community, member, people, adult);
    }
}

public sealed class ListCommunityPostsQueryHandler(
    ICommunityRepository communities,
    IBlockDirectory blocks,
    PostViews views,
    ICurrentUser caller)
    : IRequestHandler<ListCommunityPostsQuery, PagedResult<PostDto>>
{
    public async Task<PagedResult<PostDto>> Handle(ListCommunityPostsQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        CommunityAccess.EnsureCanSeeInside(community, await communities.GetMemberAsync(community.Id, userId, cancellationToken));
        var hidden = await blocks.GetBlockedEitherWayAsync(userId, cancellationToken);
        var (items, total) = await communities.ListSharedPostsAsync(community.Id, hidden, request.Page, request.PageSize, cancellationToken);
        return new PagedResult<PostDto>(await views.BuildAsync(items, userId, cancellationToken), request.Page, request.PageSize, total);
    }
}

public sealed class ListCommunityDiscussionsQueryHandler(
    ICommunityRepository communities,
    IBlockDirectory blocks,
    IProfileDirectory profiles,
    ICurrentUser caller)
    : IRequestHandler<ListCommunityDiscussionsQuery, PagedResult<CommunityDiscussionDto>>
{
    public async Task<PagedResult<CommunityDiscussionDto>> Handle(ListCommunityDiscussionsQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var member = await communities.GetMemberAsync(community.Id, userId, cancellationToken);
        CommunityAccess.EnsureCanSeeInside(community, member);
        var hidden = await blocks.GetBlockedEitherWayAsync(userId, cancellationToken);
        var (items, total) = await communities.ListDiscussionsAsync(community.Id, hidden, request.Page, request.PageSize, cancellationToken);
        var people = await profiles.GetSummariesAsync(items.Select(d => d.AuthorId).Distinct().ToList(), cancellationToken);
        var moderates = member?.CanModerate == true;
        return new PagedResult<CommunityDiscussionDto>(
            items.Select(d => CommunityViews.Discussion(d, people, d.AuthorId == userId || moderates)).ToList(),
            request.Page,
            request.PageSize,
            total);
    }
}

public sealed class GetCommunityDiscussionQueryHandler(
    ICommunityRepository communities,
    IBlockDirectory blocks,
    IProfileDirectory profiles,
    ICurrentUser caller)
    : IRequestHandler<GetCommunityDiscussionQuery, CommunityDiscussionDetailDto>
{
    public async Task<CommunityDiscussionDetailDto> Handle(GetCommunityDiscussionQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var member = await communities.GetMemberAsync(community.Id, userId, cancellationToken);
        CommunityAccess.EnsureCanSeeInside(community, member);
        var hidden = await blocks.GetBlockedEitherWayAsync(userId, cancellationToken);
        var discussion = await communities.GetDiscussionAsync(community.Id, request.DiscussionId, cancellationToken);
        if (discussion is null || hidden.Contains(discussion.AuthorId))
        {
            throw new NotFoundException("Discussion", request.DiscussionId);
        }

        var (replies, total) = await communities.ListRepliesAsync(discussion.Id, hidden, request.Page, request.PageSize, cancellationToken);
        var people = await profiles.GetSummariesAsync(replies.Select(r => r.AuthorId).Append(discussion.AuthorId).Distinct().ToList(), cancellationToken);
        var moderates = member?.CanModerate == true;
        return new CommunityDiscussionDetailDto(
            CommunityViews.Discussion(discussion, people, discussion.AuthorId == userId || moderates),
            new PagedResult<CommunityReplyDto>(
                replies.Select(r => new CommunityReplyDto(r.Id, r.Body, AuthorDto.From(r.AuthorId, people), r.CreatedAt, r.AuthorId == userId || moderates)).ToList(),
                request.Page,
                request.PageSize,
                total));
    }
}

public sealed class ListCommunityMembersQueryHandler(ICommunityRepository communities, IProfileDirectory profiles, ICurrentUser caller)
    : IRequestHandler<ListCommunityMembersQuery, PagedResult<CommunityMemberDto>>
{
    public async Task<PagedResult<CommunityMemberDto>> Handle(ListCommunityMembersQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var community = await CommunityAccess.RequireAsync(communities, request.Slug, cancellationToken);
        var me = await communities.GetMemberAsync(community.Id, userId, cancellationToken);
        if (request.Status == CommunityMembershipStatus.Member)
        {
            CommunityAccess.EnsureCanSeeInside(community, me);
        }
        else if (me?.CanModerate != true)
        {
            throw new ForbiddenException("Only the community's admins and moderators can see requests and bans.");
        }

        var (items, total) = await communities.ListMembersAsync(community.Id, request.Status, request.Page, request.PageSize, cancellationToken);
        var people = await profiles.GetSummariesAsync(items.Select(m => m.UserId).ToList(), cancellationToken);
        return new PagedResult<CommunityMemberDto>(
            items.Select(m => new CommunityMemberDto(AuthorDto.From(m.UserId, people), m.Role, m.Status, m.JoinedAt ?? m.RequestedAt ?? m.UpdatedAt)).ToList(),
            request.Page,
            request.PageSize,
            total);
    }
}

public sealed class SuggestCommunitiesQueryHandler(ICommunityRepository communities, IPostRepository posts, ICurrentUser caller)
    : IRequestHandler<SuggestCommunitiesQuery, IReadOnlyList<CommunitySuggestionDto>>
{
    public async Task<IReadOnlyList<CommunitySuggestionDto>> Handle(SuggestCommunitiesQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var (lat, lng) = (request.Latitude, request.Longitude);
        IReadOnlyCollection<Guid> already = [];
        if (request.PostId is { } postId)
        {
            var post = await posts.GetAsync(postId, cancellationToken);
            if (post is null || post.AuthorId != userId)
            {
                throw new NotFoundException("Post", postId);
            }

            (lat, lng) = (lat ?? post.Latitude, lng ?? post.Longitude);
            already = await communities.ListSharedIntoAsync(postId, cancellationToken);
        }

        var joined = await communities.ListJoinedAsync(userId, cancellationToken);
        return joined
            .Select(c => new CommunitySuggestionDto(
                c.Id,
                c.Slug,
                c.Name,
                c.Visibility,
                c.PlaceName,
                lat is { } y && lng is { } x && c.Covers(y, x),
                already.Contains(c.Id)))
            .OrderByDescending(s => s.Recommended)
            .ThenBy(s => s.Name)
            .ToList();
    }
}

internal static class CommunityViews
{
    public static CommunityDiscussionDto Discussion(CommunityDiscussion d, IReadOnlyDictionary<Guid, ProfileSummary> people, bool canRemove) =>
        new(d.Id, d.Title, d.Body, AuthorDto.From(d.AuthorId, people), d.CreatedAt, d.LastActivityAt, d.ReplyCount, canRemove);
}
