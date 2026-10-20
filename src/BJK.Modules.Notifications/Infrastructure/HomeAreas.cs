using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Shared.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Notifications.Infrastructure;

/// <summary>
/// Home areas for other modules (For you ranking, Who to follow), from <c>notifications.AreaSubscriptions</c>. Every
/// member can choose a home area; only Premium gets alerts for it.
/// </summary>
internal sealed class HomeAreas(NotificationsDbContext db) : IHomeAreas
{
    private const double KmPerDegree = 111.32;

    public async Task<HomeArea?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.AreaSubscriptions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => new HomeArea(s.PlaceName, s.Latitude, s.Longitude, s.RadiusKm))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> UsersNearAsync(
        decimal latitude,
        decimal longitude,
        int km,
        int limit,
        CancellationToken cancellationToken)
    {
        var latDelta = (decimal)(km / KmPerDegree);
        var lngDelta = (decimal)(km / (KmPerDegree * Math.Max(Math.Cos((double)latitude * Math.PI / 180), 0.01)));
        var (minLat, maxLat, minLng, maxLng) = (latitude - latDelta, latitude + latDelta, longitude - lngDelta, longitude + lngDelta);

        // A box first (indexed columns), then nearest first by a flat-earth distance, which is plenty at this scale.
        var scale = (decimal)Math.Cos((double)latitude * Math.PI / 180);
        return await db.AreaSubscriptions.AsNoTracking()
            .Where(s => s.Latitude >= minLat && s.Latitude <= maxLat && s.Longitude >= minLng && s.Longitude <= maxLng)
            .OrderBy(s => ((s.Latitude - latitude) * (s.Latitude - latitude))
                + ((s.Longitude - longitude) * scale * ((s.Longitude - longitude) * scale)))
            .ThenBy(s => s.UserId)
            .Select(s => s.UserId)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
