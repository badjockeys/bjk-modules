using BJK.Modules.Posts.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>The blocked web addresses.</summary>
public interface IBlockedUrlRepository
{
    Task<(IReadOnlyList<BlockedUrl> Items, int TotalCount)> ListAsync(string? source, int skip, int take, CancellationToken cancellationToken);

    Task<BlockedUrl?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string source, string pattern, CancellationToken cancellationToken);

    void Add(BlockedUrl url);

    void Remove(BlockedUrl url);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class BlockedUrlRepository(PostsDbContext db) : IBlockedUrlRepository
{
    public async Task<(IReadOnlyList<BlockedUrl> Items, int TotalCount)> ListAsync(string? source, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.BlockedUrls.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(b => b.Source == source);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(b => b.AddedAt).ThenBy(b => b.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<BlockedUrl?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.BlockedUrls.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(string source, string pattern, CancellationToken cancellationToken) =>
        db.BlockedUrls.AnyAsync(b => b.Source == source && b.Pattern == pattern, cancellationToken);

    public void Add(BlockedUrl url) => db.BlockedUrls.Add(url);

    public void Remove(BlockedUrl url) => db.BlockedUrls.Remove(url);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
