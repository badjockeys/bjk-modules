using BJK.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Media.Infrastructure.Data;

public interface IMediaRepository
{
    /// <summary>Any media except deleted.</summary>
    Task<MediaItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>A user's media (not deleted), newest first.</summary>
    Task<(IReadOnlyList<MediaItem> Items, int TotalCount)> ListByUserAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Media in one state, oldest first (e.g. the review queue).</summary>
    Task<(IReadOnlyList<MediaItem> Items, int TotalCount)> ListByStatusAsync(
        MediaStatus status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Several media by id, except deleted. Tracked, so changes can be saved.</summary>
    Task<IReadOnlyList<MediaItem>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Permanently removes every media row of a user, including deleted ones.</summary>
    Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The media's status and revision as saved now (not tracked), or null if it's gone.</summary>
    Task<(MediaStatus Status, int Revision)?> GetSavedStateAsync(Guid id, CancellationToken cancellationToken);

    void Add(MediaItem media);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class MediaRepository(MediaDbContext db) : IMediaRepository
{
    public Task<MediaItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Media.FirstOrDefaultAsync(m => m.Id == id && m.Status != MediaStatus.Deleted, cancellationToken);

    public async Task<(IReadOnlyList<MediaItem> Items, int TotalCount)> ListByUserAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Media.AsNoTracking().Where(m => m.UserId == userId && m.Status != MediaStatus.Deleted);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<(IReadOnlyList<MediaItem> Items, int TotalCount)> ListByStatusAsync(
        MediaStatus status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Media.AsNoTracking().Where(m => m.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyList<MediaItem>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken) =>
        await db.Media
            .Where(m => ids.Contains(m.Id) && m.Status != MediaStatus.Deleted)
            .ToListAsync(cancellationToken);

    public Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Media.Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);

    public async Task<(MediaStatus Status, int Revision)?> GetSavedStateAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Media.AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new { m.Status, m.Revision })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.Status, row.Revision);
    }

    public void Add(MediaItem media) => db.Media.Add(media);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
