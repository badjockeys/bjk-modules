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

    /// <summary>Any comment by id, unless deleted. Tracked. For moderators.</summary>
    Task<Comment?> FindAsync(Guid commentId, CancellationToken cancellationToken);

    /// <summary>A post's visible comments, oldest first, leaving out the given authors (people the viewer blocked).</summary>
    Task<(IReadOnlyList<Comment> Items, int TotalCount)> ListVisibleAsync(
        Guid postId,
        IReadOnlyCollection<Guid> excludeAuthorIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Comments in a content screening queue, oldest first.</summary>
    Task<(IReadOnlyList<Comment> Items, int TotalCount)> ListForScreeningAsync(
        ScreeningQueue queue,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>How many comments the author has written since the given time, including deleted ones.</summary>
    Task<int> CountCreatedSinceAsync(Guid authorId, DateTimeOffset since, CancellationToken cancellationToken);

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

    /// <summary>Reports in a status, urgent first, then by when a decision is due.</summary>
    Task<(IReadOnlyList<Report> Items, int TotalCount)> ListAsync(
        ReportStatus status,
        ReportPriority? priority,
        ReportTargetType? targetType,
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

    public Task<Comment?> FindAsync(Guid commentId, CancellationToken cancellationToken) =>
        db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.Status != CommentStatus.Deleted, cancellationToken);

    public async Task<(IReadOnlyList<Comment> Items, int TotalCount)> ListForScreeningAsync(
        ScreeningQueue queue,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = queue == ScreeningQueue.Held
            ? db.Comments.AsNoTracking().Where(c => c.Status == CommentStatus.HeldForReview)
            : db.Comments.AsNoTracking().Where(c => c.IsFlagged && c.Status == CommentStatus.Visible);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<int> CountCreatedSinceAsync(Guid authorId, DateTimeOffset since, CancellationToken cancellationToken) =>
        db.Comments.CountAsync(c => c.AuthorId == authorId && c.CreatedAt >= since, cancellationToken);

    public async Task<(IReadOnlyList<Comment> Items, int TotalCount)> ListVisibleAsync(
        Guid postId,
        IReadOnlyCollection<Guid> excludeAuthorIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Comments.AsNoTracking()
            .Where(c => c.PostId == postId && c.Status == CommentStatus.Visible && !c.IsAuthorDeactivated);
        if (excludeAuthorIds.Count > 0)
        {
            query = query.Where(c => !excludeAuthorIds.Contains(c.AuthorId));
        }

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
        ReportPriority? priority,
        ReportTargetType? targetType,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Reports.AsNoTracking().Where(r => r.Status == status);
        if (priority is { } p)
        {
            query = query.Where(r => r.Priority == p);
        }

        if (targetType is { } t)
        {
            query = query.Where(r => r.TargetType == t);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(r => r.Priority == ReportPriority.Urgent ? 0 : 1)
            .ThenBy(r => r.DueAt)
            .ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public void Add(Report report) => db.Reports.Add(report);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

public interface IAppealRepository
{
    Task<Appeal?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid appellantId, ReportTargetType targetType, Guid targetId, CancellationToken cancellationToken);

    /// <summary>Appeals in a status, oldest due first.</summary>
    Task<(IReadOnlyList<Appeal> Items, int TotalCount)> ListAsync(
        AppealStatus status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    void Add(Appeal appeal);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Filters for the moderation audit log. Every filter is optional.</summary>
public sealed record AuditSearch(
    Guid? ActorId = null,
    string? Module = null,
    string? Action = null,
    string? TargetType = null,
    string? TargetId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public interface IModerationAuditRepository
{
    void Add(ModerationAuditRecord record);

    /// <summary>Entries newest first.</summary>
    Task<(IReadOnlyList<ModerationAuditRecord> Items, int TotalCount)> SearchAsync(
        AuditSearch search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class AppealRepository(PostsDbContext db) : IAppealRepository
{
    public Task<Appeal?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Appeals.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(Guid appellantId, ReportTargetType targetType, Guid targetId, CancellationToken cancellationToken) =>
        db.Appeals.AnyAsync(
            a => a.AppellantId == appellantId && a.TargetType == targetType && a.TargetId == targetId,
            cancellationToken);

    public async Task<(IReadOnlyList<Appeal> Items, int TotalCount)> ListAsync(
        AppealStatus status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Appeals.AsNoTracking().Where(a => a.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(a => a.DueAt)
            .ThenBy(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public void Add(Appeal appeal) => db.Appeals.Add(appeal);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

internal sealed class ModerationAuditRepository(PostsDbContext db) : IModerationAuditRepository
{
    public void Add(ModerationAuditRecord record) => db.ModerationAuditLog.Add(record);

    public async Task<(IReadOnlyList<ModerationAuditRecord> Items, int TotalCount)> SearchAsync(
        AuditSearch search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.ModerationAuditLog.AsNoTracking();
        if (search.ActorId is { } actorId)
        {
            query = query.Where(a => a.ActorId == actorId);
        }

        if (!string.IsNullOrWhiteSpace(search.Module))
        {
            query = query.Where(a => a.Module == search.Module);
        }

        if (!string.IsNullOrWhiteSpace(search.Action))
        {
            query = query.Where(a => a.Action == search.Action);
        }

        if (!string.IsNullOrWhiteSpace(search.TargetType))
        {
            query = query.Where(a => a.TargetType == search.TargetType);
        }

        if (!string.IsNullOrWhiteSpace(search.TargetId))
        {
            query = query.Where(a => a.TargetId == search.TargetId);
        }

        if (search.From is { } from)
        {
            query = query.Where(a => a.OccurredAt >= from);
        }

        if (search.To is { } to)
        {
            query = query.Where(a => a.OccurredAt < to);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(a => a.OccurredAt)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
