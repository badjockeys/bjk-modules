using BJK.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Media.Infrastructure.Data;

/// <summary>The known-image hashes and the uploads that matched them.</summary>
public interface IKnownImageRepository
{
    /// <summary>A hash row, or null if it has been removed.</summary>
    Task<KnownImageHash?> GetHashAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The hashes, newest first, optionally only one source's.</summary>
    Task<(IReadOnlyList<KnownImageHash> Items, int TotalCount)> ListHashesAsync(string? source, int skip, int take, CancellationToken cancellationToken);

    /// <summary>True if the source already has this hash.</summary>
    Task<bool> HasHashAsync(string source, KnownHashKind kind, string value, CancellationToken cancellationToken);

    /// <summary>Matches, newest first.</summary>
    Task<(IReadOnlyList<KnownImageMatch> Items, int TotalCount)> ListMatchesAsync(int skip, int take, CancellationToken cancellationToken);

    Task<KnownImageMatch?> GetMatchAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Adds a hash; saved with the next save.</summary>
    void Add(KnownImageHash hash);

    /// <summary>Records a match; saved with the next save (the media repository's too: they share a context).</summary>
    void Add(KnownImageMatch match);

    void Remove(KnownImageHash hash);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class KnownImageRepository(MediaDbContext db) : IKnownImageRepository
{
    public Task<KnownImageHash?> GetHashAsync(Guid id, CancellationToken cancellationToken) =>
        db.KnownImageHashes.FirstOrDefaultAsync(h => h.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<KnownImageHash> Items, int TotalCount)> ListHashesAsync(
        string? source,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var query = db.KnownImageHashes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(h => h.Source == source);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(h => h.AddedAt).ThenBy(h => h.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<bool> HasHashAsync(string source, KnownHashKind kind, string value, CancellationToken cancellationToken) =>
        db.KnownImageHashes.AnyAsync(h => h.Source == source && h.Kind == kind && h.Value == value, cancellationToken);

    public async Task<(IReadOnlyList<KnownImageMatch> Items, int TotalCount)> ListMatchesAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var total = await db.KnownImageMatches.CountAsync(cancellationToken);
        var items = await db.KnownImageMatches.AsNoTracking()
            .OrderByDescending(m => m.MatchedAt).ThenBy(m => m.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<KnownImageMatch?> GetMatchAsync(Guid id, CancellationToken cancellationToken) =>
        db.KnownImageMatches.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public void Add(KnownImageHash hash) => db.KnownImageHashes.Add(hash);

    public void Add(KnownImageMatch match) => db.KnownImageMatches.Add(match);

    public void Remove(KnownImageHash hash) => db.KnownImageHashes.Remove(hash);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
