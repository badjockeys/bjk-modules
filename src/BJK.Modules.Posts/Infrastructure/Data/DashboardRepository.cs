using BJK.Modules.Posts.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

public interface IDashboardRepository
{
    /// <summary>The user's dashboard items, in order (tracked). Empty if they've never changed the default layout.</summary>
    Task<IReadOnlyList<DashboardItem>> ListAsync(Guid userId, CancellationToken cancellationToken);

    void Add(DashboardItem item);

    void Remove(DashboardItem item);

    /// <summary>Deletes every dashboard item of a user (when the account is deleted).</summary>
    Task DeleteForUserAsync(Guid userId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class DashboardRepository(PostsDbContext db) : IDashboardRepository
{
    public async Task<IReadOnlyList<DashboardItem>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.DashboardItems
            .Where(d => d.UserId == userId)
            .OrderBy(d => d.Position)
            .ThenBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

    public void Add(DashboardItem item) => db.DashboardItems.Add(item);

    public void Remove(DashboardItem item) => db.DashboardItems.Remove(item);

    public Task DeleteForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.DashboardItems.Where(d => d.UserId == userId).ExecuteDeleteAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
