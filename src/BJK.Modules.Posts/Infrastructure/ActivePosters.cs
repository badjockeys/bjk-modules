using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Users;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>
/// Posting activity for Who to follow (<see cref="IActivePosters"/>): public posts only (published, media ready,
/// author active). Near a point, posts are first narrowed to a box around it in the database, then to the circle.
/// </summary>
internal sealed class ActivePosters(PostsDbContext db) : IActivePosters
{
    /// <summary>The most recent of the user's own posts looked at for where they usually post.</summary>
    public const int UsualPlacePosts = 50;

    public async Task<(decimal Latitude, decimal Longitude)?> UsualPlaceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var points = await Public()
            .Where(p => p.AuthorId == userId && p.CreatedAt >= since)
            .OrderByDescending(p => p.CreatedAt)
            .Take(UsualPlacePosts)
            .Select(p => new { p.Latitude, p.Longitude })
            .ToListAsync(cancellationToken);
        if (points.Count == 0)
        {
            return null;
        }

        // The median, so one post far from home doesn't pull it away.
        return (Median(points.Select(p => p.Latitude)), Median(points.Select(p => p.Longitude)));
    }

    public async Task<IReadOnlyList<ActivePoster>> MostActiveAsync(
        decimal? latitude,
        decimal? longitude,
        int km,
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = Public().Where(p => p.CreatedAt >= since);
        if (latitude is { } lat && longitude is { } lng)
        {
            var dLat = km / 111m;
            var dLng = km / (111m * (decimal)Math.Max(0.2, Math.Cos((double)lat * Math.PI / 180)));
            query = query.Where(p => p.Latitude >= lat - dLat && p.Latitude <= lat + dLat && p.Longitude >= lng - dLng && p.Longitude <= lng + dLng);
        }

        var posts = await query
            .Select(p => new
            {
                p.AuthorId,
                p.Latitude,
                p.Longitude,
                Engagement = db.Reactions.Count(r => r.PostId == p.Id)
                    + db.Comments.Count(c => c.PostId == p.Id && c.Status == CommentStatus.Visible),
            })
            .ToListAsync(cancellationToken);

        return posts
            .Where(p => latitude is null || longitude is null || GeoDistance.Km(latitude.Value, longitude.Value, p.Latitude, p.Longitude) <= km)
            .GroupBy(p => p.AuthorId)
            .Select(g => new ActivePoster(g.Key, g.Count(), g.Sum(p => p.Engagement)))
            .OrderByDescending(p => p.Posts)
            .ThenByDescending(p => p.Engagement)
            .ThenBy(p => p.UserId)
            .Take(limit)
            .ToList();
    }

    private IQueryable<Post> Public() =>
        db.Posts.AsNoTracking()
            .Where(p => p.Status == PostStatus.Published && p.MediaState == PostMediaState.Ready && !p.IsAuthorDeactivated);

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return Math.Round(median, 2);
    }
}
