using BJK.Modules.Notifications.Domain;
using BJK.Modules.Shared.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Notifications.Infrastructure.Data;

public interface INotificationRepository
{
    void Add(Notification notification);

    /// <summary>The user's unread notification of this type about this post, if any (for grouping reactions).</summary>
    Task<Notification?> FindUnreadAsync(Guid userId, NotificationType type, Guid postId, CancellationToken cancellationToken);

    /// <summary>The user's notifications, most recently changed first.</summary>
    Task<(IReadOnlyList<Notification> Items, int TotalCount)> ListAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Marks these (or, with null, all) of the user's notifications read.</summary>
    Task MarkReadAsync(Guid userId, IReadOnlyCollection<Guid>? ids, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The user's settings that differ from the defaults (tracked).</summary>
    Task<IReadOnlyList<NotificationSetting>> GetSettingsAsync(Guid userId, CancellationToken cancellationToken);

    void AddSetting(NotificationSetting setting);

    Task<NotificationContact?> GetContactAsync(Guid userId, CancellationToken cancellationToken);

    void AddContact(NotificationContact contact);

    void RemoveContact(NotificationContact contact);

    /// <summary>The user's push devices (tracked).</summary>
    Task<IReadOnlyList<PushSubscription>> ListPushSubscriptionsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The device with this endpoint, whoever it belongs to (tracked).</summary>
    Task<PushSubscription?> GetPushSubscriptionAsync(string endpoint, CancellationToken cancellationToken);

    void AddPushSubscription(PushSubscription subscription);

    void RemovePushSubscription(PushSubscription subscription);

    /// <summary>Deletes notifications created before <paramref name="before"/>. Returns how many.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>Everything held for the user (account deleted).</summary>
    Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class NotificationRepository(NotificationsDbContext db) : INotificationRepository
{
    public void Add(Notification notification) => db.Notifications.Add(notification);

    public Task<Notification?> FindUnreadAsync(Guid userId, NotificationType type, Guid postId, CancellationToken cancellationToken) =>
        db.Notifications
            .Where(n => n.UserId == userId && n.Type == type && n.PostId == postId && n.ReadAt == null)
            .OrderByDescending(n => n.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<(IReadOnlyList<Notification> Items, int TotalCount)> ListAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(n => n.UpdatedAt)
            .ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<int> CountUnreadAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);

    public Task MarkReadAsync(Guid userId, IReadOnlyCollection<Guid>? ids, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var query = db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null);
        if (ids is not null)
        {
            query = query.Where(n => ids.Contains(n.Id));
        }

        return query.ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationSetting>> GetSettingsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Settings.Where(s => s.UserId == userId).ToListAsync(cancellationToken);

    public void AddSetting(NotificationSetting setting) => db.Settings.Add(setting);

    public Task<NotificationContact?> GetContactAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Contacts.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);

    public void AddContact(NotificationContact contact) => db.Contacts.Add(contact);

    public void RemoveContact(NotificationContact contact) => db.Contacts.Remove(contact);

    public async Task<IReadOnlyList<PushSubscription>> ListPushSubscriptionsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.PushSubscriptions.Where(s => s.UserId == userId).OrderBy(s => s.UpdatedAt).ToListAsync(cancellationToken);

    public Task<PushSubscription?> GetPushSubscriptionAsync(string endpoint, CancellationToken cancellationToken) =>
        db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, cancellationToken);

    public void AddPushSubscription(PushSubscription subscription) => db.PushSubscriptions.Add(subscription);

    public void RemovePushSubscription(PushSubscription subscription) => db.PushSubscriptions.Remove(subscription);

    public Task<int> DeleteOlderThanAsync(DateTimeOffset before, CancellationToken cancellationToken) =>
        db.Notifications.Where(n => n.CreatedAt < before).ExecuteDeleteAsync(cancellationToken);

    public async Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await db.Notifications.Where(n => n.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.Settings.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.Contacts.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.AreaAlertItems.Where(i => i.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.AreaSubscriptions.Where(a => a.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.PushSubscriptions.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
