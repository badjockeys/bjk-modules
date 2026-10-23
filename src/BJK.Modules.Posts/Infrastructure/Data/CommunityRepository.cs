using BJK.Modules.Posts.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>Which communities to list.</summary>
public enum CommunityListKind
{
    /// <summary>Communities whose area covers a point (the viewer's home area), most members first.</summary>
    Near,

    /// <summary>The viewer's own: member of, admin or moderator.</summary>
    Mine,

    /// <summary>Communities without an area (about a topic rather than a place), most members first.</summary>
    Topics,

    /// <summary>Name, description or place containing the search text, most members first.</summary>
    Search,
}

public sealed record CommunityListQuery(
    CommunityListKind Kind,
    Guid? ViewerId = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    string? Search = null);

/// <summary>Communities, their members, shared posts, discussions and replies.</summary>
public interface ICommunityRepository
{
    /// <summary>A community that hasn't been removed, by its address; tracked.</summary>
    Task<Community?> GetBySlugAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Any community, removed or not, by id; tracked.</summary>
    Task<Community?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> SlugTakenAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Communities someone made that haven't been removed.</summary>
    Task<int> CountOwnedAsync(Guid ownerId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Community> Items, int TotalCount)> ListAsync(CommunityListQuery query, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Every community the user is a member of (not removed), for suggestions when posting.</summary>
    Task<IReadOnlyList<Community>> ListJoinedAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The user's places in these communities, by community id.</summary>
    Task<IReadOnlyDictionary<Guid, CommunityMember>> GetMembershipsAsync(Guid userId, IReadOnlyCollection<Guid> communityIds, CancellationToken cancellationToken);

    Task<CommunityMember?> GetMemberAsync(Guid communityId, Guid userId, CancellationToken cancellationToken);

    /// <summary>People in a community with this status, newest first (members: admins and moderators first).</summary>
    Task<(IReadOnlyList<CommunityMember> Items, int TotalCount)> ListMembersAsync(Guid communityId, CommunityMembershipStatus status, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Members with a role (admins and moderators).</summary>
    Task<int> CountAdminsAsync(Guid communityId, CancellationToken cancellationToken);

    void Add(Community community);

    void Add(CommunityMember member);

    void Remove(CommunityMember member);

    /// <summary>Public posts (published, media ready, author active) shared into a community, newest share first.</summary>
    Task<(IReadOnlyList<Post> Items, int TotalCount)> ListSharedPostsAsync(Guid communityId, IReadOnlyCollection<Guid> hiddenAuthors, int page, int pageSize, CancellationToken cancellationToken);

    Task<CommunityPost?> GetShareAsync(Guid communityId, Guid postId, CancellationToken cancellationToken);

    /// <summary>The communities a post has been shared into (not removed).</summary>
    Task<IReadOnlyList<Guid>> ListSharedIntoAsync(Guid postId, CancellationToken cancellationToken);

    void Add(CommunityPost share);

    void Remove(CommunityPost share);

    Task<(IReadOnlyList<CommunityDiscussion> Items, int TotalCount)> ListDiscussionsAsync(Guid communityId, IReadOnlyCollection<Guid> hiddenAuthors, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>A discussion that hasn't been removed; tracked.</summary>
    Task<CommunityDiscussion?> GetDiscussionAsync(Guid communityId, Guid discussionId, CancellationToken cancellationToken);

    /// <summary>Any discussion by id (for moderators acting on a report); tracked.</summary>
    Task<CommunityDiscussion?> FindDiscussionAsync(Guid discussionId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<CommunityReply> Items, int TotalCount)> ListRepliesAsync(Guid discussionId, IReadOnlyCollection<Guid> hiddenAuthors, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Any reply by id; tracked.</summary>
    Task<CommunityReply?> FindReplyAsync(Guid replyId, CancellationToken cancellationToken);

    void Add(CommunityDiscussion discussion);

    void Add(CommunityReply reply);

    /// <summary>
    /// With the account: removes the user's memberships (adjusting counts), discussions, replies and shares, and
    /// removes the communities they made.
    /// </summary>
    Task DeleteAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class CommunityRepository(PostsDbContext db) : ICommunityRepository
{
    public Task<Community?> GetBySlugAsync(string slug, CancellationToken cancellationToken) =>
        db.Communities.SingleOrDefaultAsync(c => c.Slug == slug && c.RemovedAt == null, cancellationToken);

    public Task<Community?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Communities.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<bool> SlugTakenAsync(string slug, CancellationToken cancellationToken) =>
        db.Communities.AnyAsync(c => c.Slug == slug, cancellationToken);

    public Task<int> CountOwnedAsync(Guid ownerId, CancellationToken cancellationToken) =>
        db.Communities.CountAsync(c => c.OwnerId == ownerId && c.RemovedAt == null, cancellationToken);

    public async Task<(IReadOnlyList<Community> Items, int TotalCount)> ListAsync(CommunityListQuery query, int page, int pageSize, CancellationToken cancellationToken)
    {
        var communities = db.Communities.AsNoTracking().Where(c => c.RemovedAt == null);
        switch (query.Kind)
        {
            case CommunityListKind.Mine:
                var mine = db.CommunityMembers.Where(m => m.UserId == query.ViewerId && m.Status == CommunityMembershipStatus.Member).Select(m => m.CommunityId);
                communities = communities.Where(c => mine.Contains(c.Id));
                break;
            case CommunityListKind.Topics:
                communities = communities.Where(c => c.RadiusKm == null);
                break;
            case CommunityListKind.Search:
                var text = (query.Search ?? string.Empty).Trim();
                communities = communities.Where(c => c.Name.Contains(text) || c.Description.Contains(text) || (c.PlaceName != null && c.PlaceName.Contains(text)));
                break;
            case CommunityListKind.Near:
                if (query.Latitude is not { } lat || query.Longitude is not { } lng)
                {
                    return ([], 0);
                }

                // A box the largest radius (50 km) wide, then the exact distance in memory.
                var box = GeoBox.Around(lat, lng, Community.Radiuses.Max());
                var candidates = await communities
                    .Where(c => c.Latitude != null && c.Latitude >= box.MinLatitude && c.Latitude <= box.MaxLatitude
                        && c.Longitude >= box.MinLongitude && c.Longitude <= box.MaxLongitude)
                    .OrderByDescending(c => c.MemberCount)
                    .Take(500)
                    .ToListAsync(cancellationToken);
                var covering = candidates.Where(c => c.Covers(lat, lng)).ToList();
                return (covering.Skip((page - 1) * pageSize).Take(pageSize).ToList(), covering.Count);
        }

        var total = await communities.CountAsync(cancellationToken);
        var items = await communities
            .OrderByDescending(c => c.MemberCount)
            .ThenBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<Community>> ListJoinedAsync(Guid userId, CancellationToken cancellationToken)
    {
        var joined = db.CommunityMembers.Where(m => m.UserId == userId && m.Status == CommunityMembershipStatus.Member).Select(m => m.CommunityId);
        return await db.Communities.AsNoTracking()
            .Where(c => c.RemovedAt == null && joined.Contains(c.Id))
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, CommunityMember>> GetMembershipsAsync(Guid userId, IReadOnlyCollection<Guid> communityIds, CancellationToken cancellationToken) =>
        communityIds.Count == 0
            ? new Dictionary<Guid, CommunityMember>()
            : await db.CommunityMembers.AsNoTracking()
                .Where(m => m.UserId == userId && communityIds.Contains(m.CommunityId))
                .ToDictionaryAsync(m => m.CommunityId, cancellationToken);

    public Task<CommunityMember?> GetMemberAsync(Guid communityId, Guid userId, CancellationToken cancellationToken) =>
        db.CommunityMembers.SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == userId, cancellationToken);

    public async Task<(IReadOnlyList<CommunityMember> Items, int TotalCount)> ListMembersAsync(Guid communityId, CommunityMembershipStatus status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.CommunityMembers.AsNoTracking().Where(m => m.CommunityId == communityId && m.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(m => m.Role)
            .ThenByDescending(m => m.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<int> CountAdminsAsync(Guid communityId, CancellationToken cancellationToken) =>
        db.CommunityMembers.CountAsync(
            m => m.CommunityId == communityId && m.Status == CommunityMembershipStatus.Member && m.Role == CommunityRole.Admin,
            cancellationToken);

    public void Add(Community community) => db.Communities.Add(community);

    public void Add(CommunityMember member) => db.CommunityMembers.Add(member);

    public void Remove(CommunityMember member) => db.CommunityMembers.Remove(member);

    public async Task<(IReadOnlyList<Post> Items, int TotalCount)> ListSharedPostsAsync(Guid communityId, IReadOnlyCollection<Guid> hiddenAuthors, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query =
            from share in db.CommunityPosts
            join post in db.Posts on share.PostId equals post.Id
            where share.CommunityId == communityId
                && post.Status == PostStatus.Published && post.MediaState == PostMediaState.Ready && !post.IsAuthorDeactivated
                && !hiddenAuthors.Contains(post.AuthorId)
            orderby share.SharedAt descending
            select post;
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).Include(p => p.Media).AsNoTracking().ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<CommunityPost?> GetShareAsync(Guid communityId, Guid postId, CancellationToken cancellationToken) =>
        db.CommunityPosts.SingleOrDefaultAsync(s => s.CommunityId == communityId && s.PostId == postId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListSharedIntoAsync(Guid postId, CancellationToken cancellationToken) =>
        await (from share in db.CommunityPosts
               join community in db.Communities on share.CommunityId equals community.Id
               where share.PostId == postId && community.RemovedAt == null
               select share.CommunityId).ToListAsync(cancellationToken);

    public void Add(CommunityPost share) => db.CommunityPosts.Add(share);

    public void Remove(CommunityPost share) => db.CommunityPosts.Remove(share);

    public async Task<(IReadOnlyList<CommunityDiscussion> Items, int TotalCount)> ListDiscussionsAsync(Guid communityId, IReadOnlyCollection<Guid> hiddenAuthors, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.CommunityDiscussions.AsNoTracking()
            .Where(d => d.CommunityId == communityId && d.Status == CommunityContentStatus.Visible && !hiddenAuthors.Contains(d.AuthorId));
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(d => d.LastActivityAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<CommunityDiscussion?> GetDiscussionAsync(Guid communityId, Guid discussionId, CancellationToken cancellationToken) =>
        db.CommunityDiscussions.SingleOrDefaultAsync(
            d => d.Id == discussionId && d.CommunityId == communityId && d.Status == CommunityContentStatus.Visible,
            cancellationToken);

    public Task<CommunityDiscussion?> FindDiscussionAsync(Guid discussionId, CancellationToken cancellationToken) =>
        db.CommunityDiscussions.SingleOrDefaultAsync(d => d.Id == discussionId, cancellationToken);

    public async Task<(IReadOnlyList<CommunityReply> Items, int TotalCount)> ListRepliesAsync(Guid discussionId, IReadOnlyCollection<Guid> hiddenAuthors, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.CommunityReplies.AsNoTracking()
            .Where(r => r.DiscussionId == discussionId && r.Status == CommunityContentStatus.Visible && !hiddenAuthors.Contains(r.AuthorId));
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(r => r.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<CommunityReply?> FindReplyAsync(Guid replyId, CancellationToken cancellationToken) =>
        db.CommunityReplies.SingleOrDefaultAsync(r => r.Id == replyId, cancellationToken);

    public void Add(CommunityDiscussion discussion) => db.CommunityDiscussions.Add(discussion);

    public void Add(CommunityReply reply) => db.CommunityReplies.Add(reply);

    public async Task DeleteAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Communities they made go for everyone; the rest of their place in other communities is removed.
        foreach (var owned in await db.Communities.Where(c => c.OwnerId == userId && c.RemovedAt == null).ToListAsync(cancellationToken))
        {
            owned.Remove("Its admin's account was deleted.", now);
        }

        foreach (var membership in await db.CommunityMembers.Where(m => m.UserId == userId).ToListAsync(cancellationToken))
        {
            if (membership.IsMember && await db.Communities.SingleOrDefaultAsync(c => c.Id == membership.CommunityId, cancellationToken) is { } community)
            {
                community.CountMembers(-1);
            }

            db.CommunityMembers.Remove(membership);
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.CommunityReplies.Where(r => r.AuthorId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.CommunityDiscussions.Where(d => d.AuthorId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.CommunityPosts.Where(s => s.SharedBy == userId).ExecuteDeleteAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
