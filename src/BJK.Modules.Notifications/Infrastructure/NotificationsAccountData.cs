using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Notifications.Infrastructure;

/// <summary>Deletes the user's notifications, settings, saved email address and push devices with their account.</summary>
internal sealed class NotificationsUserLifecycleHandler(INotificationRepository notifications) : IUserLifecycleHandler
{
    public Task HandleAsync(UserLifecycleEvent userEvent, CancellationToken cancellationToken) =>
        userEvent.Type == UserLifecycleEventType.Deleted
            ? notifications.DeleteAllForUserAsync(userEvent.UserId, cancellationToken)
            : Task.CompletedTask;
}

/// <summary>The user's notifications, their settings, the email address notifications go to and their push devices.</summary>
internal sealed class NotificationsPersonalDataExporter(NotificationsDbContext db) : IPersonalDataExporter
{
    public string Section => "notifications";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var notifications = (await db.Notifications.AsNoTracking()
                .Where(n => n.UserId == userId)
                .OrderBy(n => n.CreatedAt)
                .ToListAsync(cancellationToken))
            .Select(n => new { n.Id, Type = n.Type.ToString(), n.ActorId, n.PostId, n.CommentId, n.PostTitle, n.Detail, n.Count, n.CreatedAt, n.UpdatedAt, n.ReadAt })
            .ToList();
        var settings = (await db.Settings.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(cancellationToken))
            .Select(s => new { Type = s.Type.ToString(), s.InApp, s.Email, s.Push })
            .ToList();
        var contact = await db.Contacts.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.Email, c.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        var homeArea = await db.AreaSubscriptions.AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => new { a.PlaceName, a.Latitude, a.Longitude, a.RadiusKm, Mode = a.Mode.ToString(), a.Enabled, a.Email, a.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        // The devices, without their keys (which only the push service and the browser need).
        var pushDevices = await db.PushSubscriptions.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new { p.Endpoint, p.CreatedAt, p.UpdatedAt })
            .ToListAsync(cancellationToken);
        return new { notifications, settings, emailAddress = contact, homeArea, pushDevices };
    }
}
