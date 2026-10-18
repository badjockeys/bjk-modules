using BJK.Modules.Posts.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

public interface IReactionRepository
{
    Task<Reaction?> GetAsync(Guid postId, Guid userId, CancellationToken cancellationToken);

    void Add(Reaction reaction);

    void Remove(Reaction reaction);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ICommentRepository
{
    /// <summary>A comment on the given post, unless deleted. Tracked.</summary>
    Task<Comment?> GetAsync(Guid postId, Guid commentId, CancellationToken cancellationToken);

    /// <summary>A post's visible comments, oldest first.</summary>
    Task<(IReadOnlyList<Comment> Items, int TotalCount)> ListVisibleAsync(
        Guid postId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    void Add(Comment comment);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IReportRepository
{
    Task<Report?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid reporterId, ReportTargetType targetType, Guid targetId, CancellationToken cancellationToken);

    /// <summary>Open reports about one post or comment. Tracked.</summary>
    Task<IReadOnlyList<Report>> ListOpenForTargetAsync(
        ReportTargetType targetType,
        Guid targetId,
        CancellationToken cancellationToken);

    /// <summary>Reports in a status, oldest first.</summary>
    Task<(IReadOnlyList<Report> Items, int TotalCount)> ListAsync(
        ReportStatus status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    void Add(Report report);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class ReactionRepository(PostsDbContext db) : IReactionRepository
{
    public Task<Reaction?> GetAsync(Guid postId, Guid userId, CancellationToken cancellationToken) =>
        db.Reactions.FirstOrDefaultAsync(r => r.PostId == postId && r.UserId == userId, cancellationToken);

    public void Add(Reaction reaction) => db.Reactions.Add(reaction);

    public void Remove(Reaction reaction) => db.Reactions.Remove(reaction);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

internal sealed class CommentRepository(PostsDbContext db) : ICommentRepository
{
    public Task<Comment?> GetAsync(Guid postId, Guid commentId, CancellationToken cancellationToken) =>
        db.Comments.FirstOrDefaultAsync(
            c => c.Id == commentId && c.PostId == postId && c.Status != CommentStatus.Deleted,
            cancellationToken);

    public async Task<(IReadOnlyList<Comment> Items, int TotalCount)> ListVisibleAsync(
        Guid postId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Comments.AsNoTracking()
            .Where(c => c.PostId == postId && c.Status == CommentStatus.Visible && !c.IsAuthorDeactivated);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public void Add(Comment comment) => db.Comments.Add(comment);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

internal sealed class ReportRepository(PostsDbContext db) : IReportRepository
{
    public Task<Report?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Reports.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(
        Guid reporterId,
        ReportTargetType targetType,
        Guid targetId,
        CancellationToken cancellationToken) =>
        db.Reports.AnyAsync(
            r => r.ReporterId == reporterId && r.TargetType == targetType && r.TargetId == targetId,
            cancellationToken);

    public async Task<IReadOnlyList<Report>> ListOpenForTargetAsync(
        ReportTargetType targetType,
        Guid targetId,
        CancellationToken cancellationToken) =>
        await db.Reports
            .Where(r => r.TargetType == targetType && r.TargetId == targetId && r.Status == ReportStatus.Open)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<Report> Items, int TotalCount)> ListAsync(
        ReportStatus status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Reports.AsNoTracking().Where(r => r.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public void Add(Report report) => db.Reports.Add(report);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
