using BJK.Modules.Posts.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>Which posts to list. Deleted posts are never included.</summary>
/// <param name="AuthorId">Only this author's posts.</param>
/// <param name="Category">Only this kind of incident.</param>
/// <param name="Area">Only incidents inside this box.</param>
/// <param name="IncludeHidden">Include hidden posts (for an author's own list).</param>
public sealed record PostSearch(Guid? AuthorId, IncidentCategory? Category, GeoBox? Area, bool IncludeHidden);

/// <summary>A latitude/longitude rectangle.</summary>
public sealed record GeoBox(decimal MinLatitude, decimal MaxLatitude, decimal MinLongitude, decimal MaxLongitude)
{
    private const double KmPerDegreeLatitude = 111.32;

    /// <summary>The box that contains a circle of <paramref name="radiusKm"/> around a point.</summary>
    public static GeoBox Around(decimal latitude, decimal longitude, int radiusKm)
    {
        var latDelta = radiusKm / KmPerDegreeLatitude;
        var cos = Math.Max(Math.Cos((double)latitude * Math.PI / 180), 0.01);
        var lngDelta = radiusKm / (KmPerDegreeLatitude * cos);

        return new GeoBox(
            Math.Max(-90m, latitude - (decimal)latDelta),
            Math.Min(90m, latitude + (decimal)latDelta),
            Math.Max(-180m, longitude - (decimal)lngDelta),
            Math.Min(180m, longitude + (decimal)lngDelta));
    }
}

/// <summary>Counts shown with a post.</summary>
public sealed record PostStats(IReadOnlyDictionary<ReactionType, int> Reactions, int CommentCount, ReactionType? MyReaction)
{
    public static readonly PostStats Empty = new(new Dictionary<ReactionType, int>(), 0, null);
}

public interface IPostRepository
{
    /// <summary>A post with its media, unless deleted. Tracked, so changes can be saved.</summary>
    Task<Post?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>True if any of the media is already in a post (including hidden and deleted ones).</summary>
    Task<bool> AnyUseMediaAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);

    /// <summary>Posts newest first.</summary>
    Task<(IReadOnlyList<Post> Items, int TotalCount)> SearchAsync(
        PostSearch search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Reaction and comment counts for each post, and the viewer's own reaction.</summary>
    Task<IReadOnlyDictionary<Guid, PostStats>> GetStatsAsync(
        IReadOnlyCollection<Guid> postIds,
        Guid? viewerId,
        CancellationToken cancellationToken);

    void Add(Post post);

    /// <summary>Saves all pending changes in the Posts module (posts, reactions, comments, reports).</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class PostRepository(PostsDbContext db) : IPostRepository
{
    public Task<Post?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Posts
            .Include(p => p.Media)
            .FirstOrDefaultAsync(p => p.Id == id && p.Status != PostStatus.Deleted, cancellationToken);

    public Task<bool> AnyUseMediaAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken) =>
        db.Set<PostMedia>().AnyAsync(m => mediaIds.Contains(m.MediaId), cancellationToken);

    public async Task<(IReadOnlyList<Post> Items, int TotalCount)> SearchAsync(
        PostSearch search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Posts.AsNoTracking().Where(p => p.Status != PostStatus.Deleted);

        if (!search.IncludeHidden)
        {
            query = query.Where(p => p.Status == PostStatus.Published);
        }

        if (search.AuthorId is { } authorId)
        {
            query = query.Where(p => p.AuthorId == authorId);
        }

        if (search.Category is { } category)
        {
            query = query.Where(p => p.Category == category);
        }

        if (search.Area is { } area)
        {
            query = query.Where(p =>
                p.Latitude >= area.MinLatitude && p.Latitude <= area.MaxLatitude &&
                p.Longitude >= area.MinLongitude && p.Longitude <= area.MaxLongitude);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(p => p.Media)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, PostStats>> GetStatsAsync(
        IReadOnlyCollection<Guid> postIds,
        Guid? viewerId,
        CancellationToken cancellationToken)
    {
        if (postIds.Count == 0)
        {
            return new Dictionary<Guid, PostStats>();
        }

        var reactions = await db.Reactions
            .Where(r => postIds.Contains(r.PostId))
            .GroupBy(r => new { r.PostId, r.Type })
            .Select(g => new { g.Key.PostId, g.Key.Type, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var comments = await db.Comments
            .Where(c => postIds.Contains(c.PostId) && c.Status == CommentStatus.Visible)
            .GroupBy(c => c.PostId)
            .Select(g => new { PostId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var mine = new Dictionary<Guid, ReactionType>();
        if (viewerId is { } userId)
        {
            var rows = await db.Reactions
                .Where(r => r.UserId == userId && postIds.Contains(r.PostId))
                .Select(r => new { r.PostId, r.Type })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                mine[row.PostId] = row.Type;
            }
        }

        return postIds.Distinct().ToDictionary(
            id => id,
            id => new PostStats(
                reactions.Where(r => r.PostId == id).ToDictionary(r => r.Type, r => r.Count),
                comments.FirstOrDefault(c => c.PostId == id)?.Count ?? 0,
                mine.TryGetValue(id, out var type) ? type : null));
    }

    public void Add(Post post) => db.Posts.Add(post);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
