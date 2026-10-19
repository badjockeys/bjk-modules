using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>Hides a deactivated user's posts and comments, shows them again on return, and deletes them with the account.</summary>
internal sealed class PostsUserLifecycleHandler(IPostRepository posts) : IUserLifecycleHandler
{
    public Task HandleAsync(UserLifecycleEvent userEvent, CancellationToken cancellationToken) => userEvent.Type switch
    {
        UserLifecycleEventType.Deactivated => posts.SetAuthorDeactivatedAsync(userEvent.UserId, true, cancellationToken),
        UserLifecycleEventType.Reactivated => posts.SetAuthorDeactivatedAsync(userEvent.UserId, false, cancellationToken),
        UserLifecycleEventType.Deleted => posts.DeleteAllForUserAsync(userEvent.UserId, cancellationToken),
        _ => Task.CompletedTask,
    };
}

/// <summary>The user's posts (including hidden and deleted ones still held), comments, reactions and reports.</summary>
internal sealed class PostsPersonalDataExporter(PostsDbContext db) : IPersonalDataExporter
{
    public string Section => "posts";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var posts = await db.Posts.AsNoTracking()
            .Where(p => p.AuthorId == userId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.Description,
                p.Category,
                p.IncidentAt,
                p.Latitude,
                p.Longitude,
                p.PlaceName,
                p.Status,
                p.IsSensitive,
                p.HiddenReason,
                p.CreatedAt,
                p.UpdatedAt,
                p.DeletedAt,
                MediaIds = p.Media.OrderBy(m => m.Position).Select(m => m.MediaId).ToList(),
            })
            .ToListAsync(cancellationToken);

        var comments = await db.Comments.AsNoTracking()
            .Where(c => c.AuthorId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.Id, c.PostId, c.ParentCommentId, c.Body, c.Status, c.CreatedAt, c.UpdatedAt, c.DeletedAt })
            .ToListAsync(cancellationToken);

        var reactions = await db.Reactions.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.PostId, r.Type, r.CreatedAt })
            .ToListAsync(cancellationToken);

        var commentReactions = await db.CommentReactions.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.CommentId, r.Type, r.CreatedAt })
            .ToListAsync(cancellationToken);

        var reports = await db.Reports.AsNoTracking()
            .Where(r => r.ReporterId == userId)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.Id, r.TargetType, r.TargetId, r.PostId, r.Reason, r.Details, r.ContactEmail, r.Status, r.CreatedAt, r.ResolvedAt })
            .ToListAsync(cancellationToken);

        var appeals = await db.Appeals.AsNoTracking()
            .Where(a => a.AppellantId == userId)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.TargetType, a.TargetId, a.PostId, a.Statement, a.ContactEmail, a.Status, a.CreatedAt, a.DecidedAt })
            .ToListAsync(cancellationToken);

        return new { posts, comments, reactions, commentReactions, reportsMade = reports, appeals };
    }
}
