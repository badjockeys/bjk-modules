using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Notifications.Infrastructure;

/// <summary>Deletes the user's notifications, settings and saved email address with their account.</summary>
internal sealed class NotificationsUserLifecycleHandler(INotificationRepository notifications) : IUserLifecycleHandler
{
    public Task HandleAsync(UserLifecycleEvent userEvent, CancellationToken cancellationToken) =>
        userEvent.Type == UserLifecycleEventType.Deleted
            ? notifications.DeleteAllForUserAsync(userEvent.UserId, cancellationToken)
            : Task.CompletedTask;
}

/// <summary>The user's notifications, their settings and the email address notifications go to.</summary>
internal sealed class NotificationsPersonalDataExporter(NotificationsDbContext db) : IPersonalDataExporter
{
    public string Section => "notifications";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var notifications = (await db.Notifications.AsNoTracking()
                .Where(n => n.UserId == userId)
                .OrderBy(n => n.CreatedAt)
                .ToListAsync(cancellationToken))
            .Select(n => new { n.Id, Type = n.Type.ToString(), n.ActorId, n.PostId, n.CommentId, n.PostTitle, n.Count, n.CreatedAt, n.UpdatedAt, n.ReadAt })
            .ToList();
        var settings = (await db.Settings.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(cancellationToken))
            .Select(s => new { Type = s.Type.ToString(), s.InApp, s.Email })
            .ToList();
        var contact = await db.Contacts.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.Email, c.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        return new { notifications, settings, emailAddress = contact };
    }
}
