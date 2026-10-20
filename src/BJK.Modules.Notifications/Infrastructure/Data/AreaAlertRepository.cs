using BJK.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Notifications.Infrastructure.Data;

public interface IAreaAlertRepository
{
    Task<AreaSubscription?> GetAsync(Guid userId, CancellationToken cancellationToken);

    void Add(AreaSubscription subscription);

    /// <summary>Removes the subscription and anything waiting for its summary.</summary>
    Task RemoveAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Switched-on, Premium subscriptions whose home area is within <paramref name="km"/> of the point (a box; tracked).</summary>
    Task<IReadOnlyList<AreaSubscription>> FindAroundAsync(decimal latitude, decimal longitude, int km, CancellationToken cancellationToken);

    void AddItem(AreaAlertItem item);

    /// <summary>Subscriptions with posts waiting for their summary (tracked).</summary>
    Task<IReadOnlyList<AreaSubscription>> ListWithWaitingAsync(CancellationToken cancellationToken);

    /// <summary>The user's waiting posts created before <paramref name="before"/>, oldest first (tracked).</summary>
    Task<IReadOnlyList<AreaAlertItem>> GetWaitingAsync(Guid userId, DateTimeOffset before, CancellationToken cancellationToken);

    void RemoveItems(IEnumerable<AreaAlertItem> items);

    /// <summary>Waiting posts nobody will get (older than <paramref name="before"/>). Returns how many.</summary>
    Task<int> DeleteItemsOlderThanAsync(DateTimeOffset before, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class AreaAlertRepository(NotificationsDbContext db) : IAreaAlertRepository
{
    private const double KmPerDegree = 111.32;

    public Task<AreaSubscription?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        db.AreaSubscriptions.FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

    public void Add(AreaSubscription subscription) => db.AreaSubscriptions.Add(subscription);

    public async Task RemoveAsync(Guid userId, CancellationToken cancellationToken)
    {
        await db.AreaAlertItems.Where(i => i.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.AreaSubscriptions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AreaSubscription>> FindAroundAsync(
        decimal latitude,
        decimal longitude,
        int km,
        CancellationToken cancellationToken)
    {
        var latDelta = (decimal)(km / KmPerDegree);
        var lngDelta = (decimal)(km / (KmPerDegree * Math.Max(Math.Cos((double)latitude * Math.PI / 180), 0.01)));
        var (minLat, maxLat, minLng, maxLng) = (latitude - latDelta, latitude + latDelta, longitude - lngDelta, longitude + lngDelta);
        return await db.AreaSubscriptions
            .Where(s => s.Enabled && s.IsPremium)
            .Where(s => s.Latitude >= minLat && s.Latitude <= maxLat && s.Longitude >= minLng && s.Longitude <= maxLng)
            .ToListAsync(cancellationToken);
    }

    public void AddItem(AreaAlertItem item) => db.AreaAlertItems.Add(item);

    public async Task<IReadOnlyList<AreaSubscription>> ListWithWaitingAsync(CancellationToken cancellationToken) =>
        await db.AreaSubscriptions
            .Where(s => db.AreaAlertItems.Any(i => i.UserId == s.UserId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AreaAlertItem>> GetWaitingAsync(Guid userId, DateTimeOffset before, CancellationToken cancellationToken) =>
        await db.AreaAlertItems
            .Where(i => i.UserId == userId && i.CreatedAt < before)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

    public void RemoveItems(IEnumerable<AreaAlertItem> items) => db.AreaAlertItems.RemoveRange(items);

    public Task<int> DeleteItemsOlderThanAsync(DateTimeOffset before, CancellationToken cancellationToken) =>
        db.AreaAlertItems.Where(i => i.CreatedAt < before).ExecuteDeleteAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
