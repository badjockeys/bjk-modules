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

    Task<bool> IsBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken);

    Task<int> CountBlocksAsync(Guid blockerId, CancellationToken cancellationToken);

    void AddBlock(Block block);

    void RemoveBlock(Block block);

    /// <summary>Deletes every block made by or against the user, when their account is deleted.</summary>
    Task DeleteBlocksForUserAsync(Guid userId, CancellationToken cancellationToken);

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

    public Task<bool> IsBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken) =>
        db.Blocks.AnyAsync(b => b.BlockerId == blockerId && b.BlockedId == blockedId, cancellationToken);

    public Task<int> CountBlocksAsync(Guid blockerId, CancellationToken cancellationToken) =>
        db.Blocks.CountAsync(b => b.BlockerId == blockerId, cancellationToken);

    public void AddBlock(Block block) => db.Blocks.Add(block);

    public void RemoveBlock(Block block) => db.Blocks.Remove(block);

    public Task DeleteBlocksForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Blocks.Where(b => b.BlockerId == userId || b.BlockedId == userId).ExecuteDeleteAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
