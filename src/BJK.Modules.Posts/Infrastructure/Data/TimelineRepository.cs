using BJK.Modules.Posts.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

public interface ITimelineRepository
{
    /// <summary>The user's custom timelines, in order (tracked).</summary>
    Task<IReadOnlyList<Timeline>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>One of the user's timelines, or null (tracked).</summary>
    Task<Timeline?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    void Add(Timeline timeline);

    void Remove(Timeline timeline);

    /// <summary>Deletes every timeline of a user (when the account is deleted).</summary>
    Task DeleteForUserAsync(Guid userId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class TimelineRepository(PostsDbContext db) : ITimelineRepository
{
    public async Task<IReadOnlyList<Timeline>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Timelines
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.Position)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<Timeline?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.Timelines.FirstOrDefaultAsync(t => t.UserId == userId && t.Id == id, cancellationToken);

    public void Add(Timeline timeline) => db.Timelines.Add(timeline);

    public void Remove(Timeline timeline) => db.Timelines.Remove(timeline);

    public Task DeleteForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Timelines.Where(t => t.UserId == userId).ExecuteDeleteAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
