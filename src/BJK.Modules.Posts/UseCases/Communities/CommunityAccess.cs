using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;

namespace BJK.Modules.Posts.UseCases.Communities;

/// <summary>What you are to a community.</summary>
public enum CommunityStanding
{
    None,
    Requested,
    Member,
    Banned,
}

/// <summary>A community in a list.</summary>
public sealed record CommunitySummaryDto(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    CommunityVisibility Visibility,
    string? PlaceName,
    int? RadiusKm,
    int MemberCount,
    CommunityStanding Standing,
    CommunityRole? Role);

/// <summary>One community, with what you may do in it.</summary>
/// <param name="CanSeeInside">Public, or you're a member: its posts, discussions and members can be shown.</param>
/// <param name="CanModerate">You're an admin or moderator: requests, removing members and content.</param>
/// <param name="IsAdmin">You're an admin: settings, roles, deleting it.</param>
/// <param name="CanJoin">You can join (public) or ask to (private: adults only).</param>
public sealed record CommunityDto(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    string? Rules,
    CommunityVisibility Visibility,
    string? PlaceName,
    decimal? Latitude,
    decimal? Longitude,
    int? RadiusKm,
    int MemberCount,
    AuthorDto Owner,
    DateTimeOffset CreatedAt,
    CommunityStanding Standing,
    CommunityRole? Role,
    bool CanSeeInside,
    bool CanModerate,
    bool IsAdmin,
    bool CanJoin);

/// <summary>Someone in a community (or asking to join, or banned).</summary>
public sealed record CommunityMemberDto(AuthorDto Person, CommunityRole Role, CommunityMembershipStatus Status, DateTimeOffset? Since);

/// <summary>A discussion. <c>CanRemove</c>: you wrote it, or you moderate the community.</summary>
public sealed record CommunityDiscussionDto(
    Guid Id,
    string Title,
    string Body,
    AuthorDto Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int ReplyCount,
    bool CanRemove);

/// <summary>A reply to a discussion.</summary>
public sealed record CommunityReplyDto(Guid Id, string Body, AuthorDto Author, DateTimeOffset CreatedAt, bool CanRemove);

/// <summary>A discussion and a page of its replies (oldest first).</summary>
public sealed record CommunityDiscussionDetailDto(CommunityDiscussionDto Discussion, PagedResult<CommunityReplyDto> Replies);

/// <summary>One of your communities, offered when sharing a post.</summary>
/// <param name="Recommended">Its area covers where the post's incident happened.</param>
/// <param name="AlreadyShared">The post is in it already.</param>
public sealed record CommunitySuggestionDto(
    Guid Id,
    string Slug,
    string Name,
    CommunityVisibility Visibility,
    string? PlaceName,
    bool Recommended,
    bool AlreadyShared);

internal static class CommunityAccess
{
    /// <summary>How many communities one person can run.</summary>
    public const int MaxOwned = 5;

    public static async Task<Community> RequireAsync(ICommunityRepository communities, string slug, CancellationToken cancellationToken) =>
        await communities.GetBySlugAsync(slug.Trim().ToLowerInvariant(), cancellationToken)
            ?? throw new NotFoundException("Community", slug);

    public static CommunityStanding StandingOf(CommunityMember? member) => member?.Status switch
    {
        CommunityMembershipStatus.Member => CommunityStanding.Member,
        CommunityMembershipStatus.Requested => CommunityStanding.Requested,
        CommunityMembershipStatus.Banned => CommunityStanding.Banned,
        _ => CommunityStanding.None,
    };

    public static bool CanSeeInside(Community community, CommunityMember? member) =>
        community.Visibility == CommunityVisibility.Public ? member?.Status != CommunityMembershipStatus.Banned : member?.IsMember == true;

    /// <summary>The caller's membership, which must be a member (403 otherwise).</summary>
    public static async Task<CommunityMember> RequireMemberAsync(ICommunityRepository communities, Community community, Guid userId, CancellationToken cancellationToken)
    {
        var member = await communities.GetMemberAsync(community.Id, userId, cancellationToken);
        return member is { IsMember: true } ? member : throw new ForbiddenException("Join the community first.");
    }

    /// <summary>The caller's membership, which must be an admin or moderator (403 otherwise).</summary>
    public static async Task<CommunityMember> RequireModeratorAsync(ICommunityRepository communities, Community community, Guid userId, CancellationToken cancellationToken)
    {
        var member = await communities.GetMemberAsync(community.Id, userId, cancellationToken);
        return member is { CanModerate: true } ? member : throw new ForbiddenException("Only the community's admins and moderators can do that.");
    }

    public static async Task<CommunityMember> RequireAdminAsync(ICommunityRepository communities, Community community, Guid userId, CancellationToken cancellationToken)
    {
        var member = await communities.GetMemberAsync(community.Id, userId, cancellationToken);
        return member is { IsAdmin: true } ? member : throw new ForbiddenException("Only the community's admins can do that.");
    }

    /// <summary>Inside a private community needs membership; anyone can see inside a public one (unless banned).</summary>
    public static void EnsureCanSeeInside(Community community, CommunityMember? member)
    {
        if (!CanSeeInside(community, member))
        {
            throw new ForbiddenException(community.Visibility == CommunityVisibility.Private
                ? "This community is private. Ask to join to see inside."
                : "You can't see this community.");
        }
    }

    /// <summary>True if the person is known to be 18 or over (their own token said so).</summary>
    public static async Task<bool> IsAdultAsync(IProfileDirectory profiles, Guid userId, CancellationToken cancellationToken) =>
        (await profiles.GetSummariesAsync([userId], cancellationToken)).TryGetValue(userId, out var p) && p.IsAdult;

    public static CommunitySummaryDto Summary(Community c, CommunityMember? member) =>
        new(c.Id, c.Slug, c.Name, c.Description, c.Visibility, c.PlaceName, c.RadiusKm, c.MemberCount, StandingOf(member),
            member?.IsMember == true ? member.Role : null);

    public static CommunityDto Detail(Community c, CommunityMember? member, IReadOnlyDictionary<Guid, ProfileSummary> people, bool viewerIsAdult)
    {
        var standing = StandingOf(member);
        var canJoin = standing == CommunityStanding.None && (c.Visibility == CommunityVisibility.Public || viewerIsAdult);
        return new CommunityDto(
            c.Id, c.Slug, c.Name, c.Description, c.Rules, c.Visibility, c.PlaceName, c.Latitude, c.Longitude, c.RadiusKm,
            c.MemberCount, AuthorDto.From(c.OwnerId, people), c.CreatedAt, standing, member?.IsMember == true ? member.Role : null,
            CanSeeInside(c, member), member?.CanModerate == true, member?.IsAdmin == true, canJoin);
    }
}
