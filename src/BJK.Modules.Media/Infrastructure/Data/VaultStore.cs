using BJK.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Media.Infrastructure.Data;

/// <summary>People's vaults (<c>media.VaultClips</c>, <c>media.VaultAccounts</c>).</summary>
public interface IVaultStore
{
    /// <summary>One clip, tracked; null if it doesn't exist.</summary>
    Task<VaultClip?> GetAsync(Guid clipId, CancellationToken cancellationToken);

    /// <summary>A person's clips, newest first (stored ones only).</summary>
    Task<(IReadOnlyList<VaultClip> Items, int TotalCount)> ListAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Space a person's vault uses, uploads in progress included.</summary>
    Task<long> UsedBytesAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Which of these media already have their original in a vault.</summary>
    Task<IReadOnlySet<Guid>> MediaInVaultAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);

    /// <summary>Every clip of a person, tracked (to follow a plan change).</summary>
    Task<IReadOnlyList<VaultClip>> AllOfUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Stored clips past their expiry, and uploads started before <paramref name="abandonedBefore"/> that never finished.</summary>
    Task<IReadOnlyList<VaultClip>> ListDueForDeletionAsync(DateTimeOffset now, DateTimeOffset abandonedBefore, int max, CancellationToken cancellationToken);

    Task<VaultAccount?> GetAccountAsync(Guid userId, CancellationToken cancellationToken);

    void Add(VaultClip clip);

    void Add(VaultAccount account);

    void Remove(VaultClip clip);

    /// <summary>Permanently removes a person's clips and account rows (the files are removed with their storage folder).</summary>
    Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class VaultStore(MediaDbContext db) : IVaultStore
{
    public Task<VaultClip?> GetAsync(Guid clipId, CancellationToken cancellationToken) =>
        db.VaultClips.SingleOrDefaultAsync(c => c.Id == clipId, cancellationToken);

    public async Task<(IReadOnlyList<VaultClip> Items, int TotalCount)> ListAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.VaultClips.AsNoTracking().Where(c => c.UserId == userId && c.Status == VaultClipStatus.Stored);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<long> UsedBytesAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.VaultClips.Where(c => c.UserId == userId).SumAsync(c => (long?)c.SizeBytes, cancellationToken) ?? 0;

    public async Task<IReadOnlySet<Guid>> MediaInVaultAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = await db.VaultClips
            .Where(c => c.MediaId != null && mediaIds.Contains(c.MediaId.Value))
            .Select(c => c.MediaId!.Value)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public async Task<IReadOnlyList<VaultClip>> AllOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.VaultClips.Where(c => c.UserId == userId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<VaultClip>> ListDueForDeletionAsync(DateTimeOffset now, DateTimeOffset abandonedBefore, int max, CancellationToken cancellationToken) =>
        await db.VaultClips
            .Where(c => (c.Status == VaultClipStatus.Stored && c.ExpiresAt <= now)
                || (c.Status == VaultClipStatus.Uploading && c.CreatedAt < abandonedBefore))
            .OrderBy(c => c.ExpiresAt)
            .Take(max)
            .ToListAsync(cancellationToken);

    public Task<VaultAccount?> GetAccountAsync(Guid userId, CancellationToken cancellationToken) =>
        db.VaultAccounts.SingleOrDefaultAsync(a => a.UserId == userId, cancellationToken);

    public void Add(VaultClip clip) => db.VaultClips.Add(clip);

    public void Add(VaultAccount account) => db.VaultAccounts.Add(account);

    public void Remove(VaultClip clip) => db.VaultClips.Remove(clip);

    public async Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await db.VaultClips.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.VaultAccounts.Where(a => a.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
