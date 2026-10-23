using BJK.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Profiles.Infrastructure.Data;

public interface IProfileRepository
{
    Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Looks up a normalised handle.</summary>
    Task<Profile?> GetByHandleAsync(string handle, CancellationToken cancellationToken);

    Task<IReadOnlyList<Profile>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    void Add(Profile profile);

    void Remove(Profile profile);

    /// <summary>One block, if the blocker has blocked that user. Tracked.</summary>
    Task<Block?> GetBlockAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken);

    /// <summary>Everyone the user has blocked, most recent first.</summary>
    Task<IReadOnlyList<Block>> ListBlocksAsync(Guid blockerId, CancellationToken cancellationToken);

    /// <summary>The ids of everyone the user has blocked.</summary>
    Task<IReadOnlyList<Guid>> GetBlockedIdsAsync(Guid blockerId, CancellationToken cancellationToken);

    /// <summary>The ids of everyone the user has blocked or been blocked by.</summary>
    Task<IReadOnlyList<Guid>> GetBlockedEitherWayAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> IsBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken);

    Task<int> CountBlocksAsync(Guid blockerId, CancellationToken cancellationToken);

    void AddBlock(Block block);

    void RemoveBlock(Block block);

    /// <summary>Deletes every block made by or against the user, when their account is deleted.</summary>
    Task DeleteBlocksForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The follow, or null (tracked).</summary>
    Task<Follow?> GetFollowAsync(Guid followerId, Guid followeeId, CancellationToken cancellationToken);

    void AddFollow(Follow follow);

    void RemoveFollow(Follow follow);

    /// <summary>Removes follows between two users, either way (when one blocks the other).</summary>
    Task RemoveFollowsBetweenAsync(Guid userId, Guid otherId, CancellationToken cancellationToken);

    /// <summary>Removes every follow to or from a user (when the account is deleted).</summary>
    Task DeleteFollowsForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>How many follow the user, and how many they follow (active profiles only).</summary>
    Task<(int Followers, int Following)> CountFollowsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Everyone the user follows (ids only), most recent first.</summary>
    Task<IReadOnlyList<Guid>> GetFollowingIdsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Of <paramref name="userIds"/>, those <paramref name="followerId"/> follows.</summary>
    Task<IReadOnlyCollection<Guid>> FollowingAmongAsync(Guid followerId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>A page of a user's followers or of who they follow, most recent first, with active profiles only.</summary>
    Task<(IReadOnlyList<Profile> Items, int TotalCount)> ListFollowsAsync(
        Guid userId,
        bool followers,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Of <paramref name="ids"/>, the active adults (known to be 18 or over), for suggestions.</summary>
    Task<IReadOnlyList<Profile>> GetSuggestableAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// People who follow the user or whom they follow, with active profiles, whose handle or name (any word of it)
    /// starts with <paramref name="text"/>: people who follow each other first, then followers, then people followed,
    /// then by name.
    /// </summary>
    Task<IReadOnlyList<TagCandidate>> SearchConnectionsAsync(Guid userId, string text, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Other active adults (known to be 18 or over) whose handle or name starts with <paramref name="text"/>, shortest
    /// handle first, leaving out <paramref name="exclude"/>.
    /// </summary>
    Task<IReadOnlyList<Profile>> SearchAdultsAsync(string text, IReadOnlyCollection<Guid> exclude, int take, CancellationToken cancellationToken);

    /// <summary>
    /// For each of <paramref name="candidateIds"/>, how many of <paramref name="followerIds"/> follow them (only
    /// those followed by at least one).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> CountFollowedByAsync(
        IReadOnlyCollection<Guid> followerIds,
        IReadOnlyCollection<Guid> candidateIds,
        CancellationToken cancellationToken);

    /// <summary>The badges a user holds.</summary>
    Task<IReadOnlyList<Badge>> ListBadgesAsync(Guid userId, CancellationToken cancellationToken);

    Task<ProfileBadge?> GetBadgeAsync(Guid userId, Badge badge, CancellationToken cancellationToken);

    void AddBadge(ProfileBadge badge);

    void RemoveBadge(ProfileBadge badge);

    /// <summary>How many profiles have the Founding member badge.</summary>
    Task<int> CountBadgeAsync(Badge badge, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class ProfileRepository(ProfilesDbContext db) : IProfileRepository
{
    public Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Profiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Profile?> GetByHandleAsync(string handle, CancellationToken cancellationToken) =>
        db.Profiles.FirstOrDefaultAsync(p => p.Handle == handle, cancellationToken);

    public async Task<IReadOnlyList<Profile>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken) =>
        await db.Profiles.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);

    public void Add(Profile profile) => db.Profiles.Add(profile);

    public async Task<IReadOnlyList<Badge>> ListBadgesAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Badges.AsNoTracking().Where(b => b.UserId == userId).Select(b => b.Badge).ToListAsync(cancellationToken);

    public Task<ProfileBadge?> GetBadgeAsync(Guid userId, Badge badge, CancellationToken cancellationToken) =>
        db.Badges.FirstOrDefaultAsync(b => b.UserId == userId && b.Badge == badge, cancellationToken);

    public void AddBadge(ProfileBadge badge) => db.Badges.Add(badge);

    public void RemoveBadge(ProfileBadge badge) => db.Badges.Remove(badge);

    public Task<int> CountBadgeAsync(Badge badge, CancellationToken cancellationToken) =>
        db.Badges.CountAsync(b => b.Badge == badge, cancellationToken);

    public void Remove(Profile profile) => db.Profiles.Remove(profile);

    public Task<Block?> GetBlockAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken) =>
        db.Blocks.FirstOrDefaultAsync(b => b.BlockerId == blockerId && b.BlockedId == blockedId, cancellationToken);

    public async Task<IReadOnlyList<Block>> ListBlocksAsync(Guid blockerId, CancellationToken cancellationToken) =>
        await db.Blocks.AsNoTracking()
            .Where(b => b.BlockerId == blockerId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetBlockedIdsAsync(Guid blockerId, CancellationToken cancellationToken) =>
        await db.Blocks.AsNoTracking()
            .Where(b => b.BlockerId == blockerId)
            .Select(b => b.BlockedId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetBlockedEitherWayAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Blocks.AsNoTracking()
            .Where(b => b.BlockerId == userId)
            .Select(b => b.BlockedId)
            .Union(db.Blocks.AsNoTracking().Where(b => b.BlockedId == userId).Select(b => b.BlockerId))
            .ToListAsync(cancellationToken);

    public Task<bool> IsBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken) =>
        db.Blocks.AnyAsync(b => b.BlockerId == blockerId && b.BlockedId == blockedId, cancellationToken);

    public Task<int> CountBlocksAsync(Guid blockerId, CancellationToken cancellationToken) =>
        db.Blocks.CountAsync(b => b.BlockerId == blockerId, cancellationToken);

    public void AddBlock(Block block) => db.Blocks.Add(block);

    public void RemoveBlock(Block block) => db.Blocks.Remove(block);

    public Task DeleteBlocksForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Blocks.Where(b => b.BlockerId == userId || b.BlockedId == userId).ExecuteDeleteAsync(cancellationToken);

    public Task<Follow?> GetFollowAsync(Guid followerId, Guid followeeId, CancellationToken cancellationToken) =>
        db.Follows.FirstOrDefaultAsync(f => f.FollowerId == followerId && f.FolloweeId == followeeId, cancellationToken);

    public void AddFollow(Follow follow) => db.Follows.Add(follow);

    public void RemoveFollow(Follow follow) => db.Follows.Remove(follow);

    public Task RemoveFollowsBetweenAsync(Guid userId, Guid otherId, CancellationToken cancellationToken) =>
        db.Follows
            .Where(f => (f.FollowerId == userId && f.FolloweeId == otherId) || (f.FollowerId == otherId && f.FolloweeId == userId))
            .ExecuteDeleteAsync(cancellationToken);

    public Task DeleteFollowsForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Follows.Where(f => f.FollowerId == userId || f.FolloweeId == userId).ExecuteDeleteAsync(cancellationToken);

    public async Task<(int Followers, int Following)> CountFollowsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var followers = await db.Follows
            .Where(f => f.FolloweeId == userId && db.Profiles.Any(p => p.Id == f.FollowerId && p.DeactivatedAt == null))
            .CountAsync(cancellationToken);
        var following = await db.Follows
            .Where(f => f.FollowerId == userId && db.Profiles.Any(p => p.Id == f.FolloweeId && p.DeactivatedAt == null))
            .CountAsync(cancellationToken);
        return (followers, following);
    }

    public async Task<IReadOnlyList<Guid>> GetFollowingIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Follows.AsNoTracking()
            .Where(f => f.FollowerId == userId)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => f.FolloweeId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<Guid>> FollowingAmongAsync(
        Guid followerId,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken) =>
        userIds.Count == 0
            ? []
            : await db.Follows.AsNoTracking()
                .Where(f => f.FollowerId == followerId && userIds.Contains(f.FolloweeId))
                .Select(f => f.FolloweeId)
                .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<Profile> Items, int TotalCount)> ListFollowsAsync(
        Guid userId,
        bool followers,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var follows = followers
            ? db.Follows.Where(f => f.FolloweeId == userId).Select(f => new { OtherId = f.FollowerId, f.CreatedAt })
            : db.Follows.Where(f => f.FollowerId == userId).Select(f => new { OtherId = f.FolloweeId, f.CreatedAt });
        var query = follows
            .Join(db.Profiles.Where(p => p.DeactivatedAt == null), f => f.OtherId, p => p.Id, (f, p) => new { f.CreatedAt, Profile = p });
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Profile.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.Profile)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<Profile>> GetSuggestableAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? []
            : await db.Profiles.AsNoTracking()
                .Where(p => ids.Contains(p.Id) && p.DeactivatedAt == null && p.IsMinor == false)
                .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TagCandidate>> SearchConnectionsAsync(Guid userId, string text, int take, CancellationToken cancellationToken)
    {
        var followers = db.Follows.Where(f => f.FolloweeId == userId).Select(f => f.FollowerId);
        var following = db.Follows.Where(f => f.FollowerId == userId).Select(f => f.FolloweeId);
        var word = " " + text;
        var rows = await db.Profiles.AsNoTracking()
            .Where(p => p.DeactivatedAt == null && p.Id != userId && (followers.Contains(p.Id) || following.Contains(p.Id)))
            .Where(p => p.Handle.StartsWith(text) || p.DisplayName.StartsWith(text) || p.DisplayName.Contains(word))
            .Select(p => new { Profile = p, FollowsYou = followers.Contains(p.Id), YouFollow = following.Contains(p.Id) })
            .OrderByDescending(x => x.FollowsYou && x.YouFollow ? 3 : x.FollowsYou ? 2 : 1)
            .ThenBy(x => x.Profile.DisplayName)
            .ThenBy(x => x.Profile.Handle)
            .Take(take)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new TagCandidate(r.Profile, r.FollowsYou, r.YouFollow)).ToList();
    }

    public async Task<IReadOnlyList<Profile>> SearchAdultsAsync(string text, IReadOnlyCollection<Guid> exclude, int take, CancellationToken cancellationToken) =>
        await db.Profiles.AsNoTracking()
            .Where(p => p.DeactivatedAt == null && p.IsMinor == false && !exclude.Contains(p.Id))
            .Where(p => p.Handle.StartsWith(text) || p.DisplayName.StartsWith(text))
            .OrderBy(p => p.Handle.Length)
            .ThenBy(p => p.Handle)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> CountFollowedByAsync(
        IReadOnlyCollection<Guid> followerIds,
        IReadOnlyCollection<Guid> candidateIds,
        CancellationToken cancellationToken) =>
        followerIds.Count == 0 || candidateIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.Follows.AsNoTracking()
                .Where(f => followerIds.Contains(f.FollowerId) && candidateIds.Contains(f.FolloweeId))
                .GroupBy(f => f.FolloweeId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>Someone connected to the user, and how: they follow the user, the user follows them, or both.</summary>
public sealed record TagCandidate(Profile Profile, bool FollowsYou, bool YouFollow);
