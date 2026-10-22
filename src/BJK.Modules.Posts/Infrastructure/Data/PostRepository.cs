using BJK.Modules.Posts.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>Which posts to list. Deleted posts are never included.</summary>
/// <param name="AuthorId">Only this author's posts.</param>
/// <param name="Category">Only posts with this type of bad driving (among their types).</param>
/// <param name="Area">Only incidents inside this box.</param>
/// <param name="IncludeHidden">Include hidden and held posts, and ones still waiting for their media (for an author's own list).</param>
/// <param name="ExcludeSensitive">Leave out sensitive posts. (The public feed no longer does: they show blurred.)</param>
/// <param name="ExcludeAuthorIds">Leave out these authors' posts (people the viewer has blocked).</param>
/// <param name="HashtagId">Only posts with this hashtag.</param>
/// <param name="AuthorIds">Only posts by these authors (the Following timeline); empty means none.</param>
/// <param name="TypeFlags">Only posts with any of these types of bad driving (<see cref="IncidentTypeFlags"/> bits; 0 = all).</param>
public sealed record PostSearch(
    Guid? AuthorId,
    IncidentCategory? Category,
    GeoBox? Area,
    bool IncludeHidden,
    bool ExcludeSensitive = false,
    IReadOnlyCollection<Guid>? ExcludeAuthorIds = null,
    Guid? HashtagId = null,
    IReadOnlyCollection<Guid>? AuthorIds = null,
    int TypeFlags = 0);

/// <summary>
/// The For you timeline: public posts from <paramref name="Since"/>, ranked by their score (reactions and comments,
/// fading with age), boosted near <paramref name="Home"/>; then older posts, newest first.
/// </summary>
/// <param name="Since">Posts since then are ranked; older ones follow by date.</param>
/// <param name="Home">The viewer's home area, or null.</param>
/// <param name="ExcludeAuthorIds">People blocked either way.</param>
public sealed record ForYouSearch(DateTimeOffset Since, ForYouHome? Home, IReadOnlyCollection<Guid>? ExcludeAuthorIds);

/// <summary>Posts in <paramref name="Near"/> score 3 times as much, and in <paramref name="Around"/> 1.5 times.</summary>
public sealed record ForYouHome(GeoBox Near, GeoBox Around);

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
public sealed record PostStats(
    IReadOnlyDictionary<ReactionType, int> Reactions,
    int CommentCount,
    ReactionType? MyReaction,
    int ShareCount = 0)
{
    public static readonly PostStats Empty = new(new Dictionary<ReactionType, int>(), 0, null);
}

public interface IPostRepository
{
    /// <summary>A post with its media, unless deleted. Tracked, so changes can be saved.</summary>
    Task<Post?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>These posts (not deleted), without media, read-only. Missing ids are left out.</summary>
    Task<IReadOnlyList<Post>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The post (not deleted) that contains this photo or video, with its media; tracked. Null if none.</summary>
    Task<Post?> FindByMediaAsync(Guid mediaId, CancellationToken cancellationToken);

    /// <summary>True if any of the media is already in a post (including hidden and deleted ones).</summary>
    Task<bool> AnyUseMediaAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);

    /// <summary>The For you timeline (see <see cref="ForYouSearch"/>).</summary>
    Task<(IReadOnlyList<Post> Items, int TotalCount)> ForYouAsync(ForYouSearch search, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Posts newest first.</summary>
    Task<(IReadOnlyList<Post> Items, int TotalCount)> SearchAsync(
        PostSearch search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Posts in a content screening queue, oldest first.</summary>
    Task<(IReadOnlyList<Post> Items, int TotalCount)> ListForScreeningAsync(
        ScreeningQueue queue,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Posts tagged sensitive automatically and waiting for a moderator to confirm or undo it, oldest first.</summary>
    Task<(IReadOnlyList<Post> Items, int TotalCount)> ListForSensitiveReviewAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>How many posts the author has created since the given time, including ones since deleted or hidden.</summary>
    Task<int> CountCreatedSinceAsync(Guid authorId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Reaction and comment counts for each post, and the viewer's own reaction.</summary>
    Task<IReadOnlyDictionary<Guid, PostStats>> GetStatsAsync(
        IReadOnlyCollection<Guid> postIds,
        Guid? viewerId,
        CancellationToken cancellationToken);

    /// <summary>Hides (or shows again) all of a user's posts and comments while their account is deactivated.</summary>
    Task SetAuthorDeactivatedAsync(Guid userId, bool deactivated, CancellationToken cancellationToken);

    /// <summary>
    /// Permanently deletes everything of a user's in the Posts module: their posts (with the comments, reactions,
    /// reports and appeals on them), their own comments, reactions (on posts and comments), reports, appeals and reel
    /// plays elsewhere, and reports about
    /// their profile. The moderation audit log is kept: it holds staff actions, not the user's content.
    /// </summary>
    Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(Post post);

    /// <summary>Saves all pending changes in the Posts module (posts, reactions, comments, reports).</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>The passenger tags on these posts (tracked, so changes are saved), any status.</summary>
    Task<IReadOnlyList<PostPassenger>> GetPassengersAsync(IReadOnlyCollection<Guid> postIds, CancellationToken cancellationToken);

    void AddPassenger(PostPassenger passenger);

    void RemovePassenger(PostPassenger passenger);

    /// <summary>
    /// Published posts (not deleted, media ready, author active) the user is tagged in with this status, newest
    /// tag first.
    /// </summary>
    Task<(IReadOnlyList<Post> Items, int TotalCount)> ListTaggedAsync(
        Guid userId,
        PassengerTagStatus status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

internal sealed class PostRepository(PostsDbContext db) : IPostRepository
{
    public async Task<IReadOnlyList<Post>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? []
            : await db.Posts.AsNoTracking()
                .Where(p => ids.Contains(p.Id) && p.Status != PostStatus.Deleted)
                .ToListAsync(cancellationToken);

    public Task<Post?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Posts
            .Include(p => p.Media)
            .FirstOrDefaultAsync(p => p.Id == id && p.Status != PostStatus.Deleted, cancellationToken);

    public Task<Post?> FindByMediaAsync(Guid mediaId, CancellationToken cancellationToken) =>
        db.Posts
            .Include(p => p.Media)
            .FirstOrDefaultAsync(p => p.Status != PostStatus.Deleted && p.Media.Any(m => m.MediaId == mediaId), cancellationToken);

    public Task<bool> AnyUseMediaAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken) =>
        db.Set<PostMedia>().AnyAsync(m => mediaIds.Contains(m.MediaId), cancellationToken);

    public async Task<(IReadOnlyList<Post> Items, int TotalCount)> ForYouAsync(
        ForYouSearch search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var visible = db.Posts.AsNoTracking()
            .Where(p => p.Status == PostStatus.Published && p.MediaState == PostMediaState.Ready && !p.IsAuthorDeactivated);
        if (search.ExcludeAuthorIds is { Count: > 0 } excluded)
        {
            visible = visible.Where(p => !excluded.Contains(p.AuthorId));
        }

        var since = search.Since;
        var recent = visible.Where(p => p.CreatedAt >= since);
        var older = visible.Where(p => p.CreatedAt < since);
        var recentCount = await recent.CountAsync(cancellationToken);
        var total = recentCount + await older.CountAsync(cancellationToken);

        IOrderedQueryable<Post> ranked;
        if (search.Home is { } home)
        {
            var (n, a) = (home.Near, home.Around);
            ranked = recent.OrderByDescending(p => p.ReelScore * (
                p.Latitude >= n.MinLatitude && p.Latitude <= n.MaxLatitude && p.Longitude >= n.MinLongitude && p.Longitude <= n.MaxLongitude
                    ? 3.0
                    : p.Latitude >= a.MinLatitude && p.Latitude <= a.MaxLatitude && p.Longitude >= a.MinLongitude && p.Longitude <= a.MaxLongitude
                        ? 1.5
                        : 1.0));
        }
        else
        {
            ranked = recent.OrderByDescending(p => p.ReelScore);
        }

        // The ranked recent posts first, then older ones by date, as one list: a page can straddle the two.
        var skip = (page - 1) * pageSize;
        var items = new List<Post>();
        if (skip < recentCount)
        {
            items.AddRange(await ranked
                .ThenByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.Id)
                .Skip(skip)
                .Take(pageSize)
                .Include(p => p.Media)
                .ToListAsync(cancellationToken));
        }

        if (items.Count < pageSize)
        {
            items.AddRange(await older
                .OrderByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.Id)
                .Skip(Math.Max(0, skip - recentCount))
                .Take(pageSize - items.Count)
                .Include(p => p.Media)
                .ToListAsync(cancellationToken));
        }

        return (items, total);
    }

    public async Task<(IReadOnlyList<Post> Items, int TotalCount)> SearchAsync(
        PostSearch search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Posts.AsNoTracking().Where(p => p.Status != PostStatus.Deleted);

        if (!search.IncludeHidden)
        {
            query = query.Where(p => p.Status == PostStatus.Published && p.MediaState == PostMediaState.Ready && !p.IsAuthorDeactivated);
        }

        if (search.ExcludeSensitive)
        {
            query = query.Where(p => !p.IsSensitive);
        }

        if (search.ExcludeAuthorIds is { Count: > 0 } excluded)
        {
            query = query.Where(p => !excluded.Contains(p.AuthorId));
        }

        if (search.AuthorId is { } authorId)
        {
            query = query.Where(p => p.AuthorId == authorId);
        }

        if (search.Category is { } category)
        {
            var bit = IncidentTypeFlags.Bit(category);
            query = query.Where(p => (p.TypeFlags & bit) != 0);
        }

        if (search.HashtagId is { } hashtagId)
        {
            query = query.Where(p => db.PostHashtags.Any(t => t.PostId == p.Id && t.HashtagId == hashtagId));
        }

        if (search.AuthorIds is { } authorIds)
        {
            if (authorIds.Count == 0)
            {
                return ([], 0);
            }

            query = query.Where(p => authorIds.Contains(p.AuthorId));
        }

        if (search.TypeFlags != 0)
        {
            var flags = search.TypeFlags;
            query = query.Where(p => (p.TypeFlags & flags) != 0);
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

    public async Task<(IReadOnlyList<Post> Items, int TotalCount)> ListForScreeningAsync(
        ScreeningQueue queue,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = queue == ScreeningQueue.Held
            ? db.Posts.AsNoTracking().Where(p => p.Status == PostStatus.HeldForReview)
            : db.Posts.AsNoTracking().Where(p => p.IsFlagged && p.Status == PostStatus.Published);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(p => p.Media)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<(IReadOnlyList<Post> Items, int TotalCount)> ListForSensitiveReviewAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Posts.AsNoTracking()
            .Where(p => p.SensitiveReview == SensitiveReviewStatus.Pending && p.Status != PostStatus.Deleted);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(p => p.Media)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<int> CountCreatedSinceAsync(Guid authorId, DateTimeOffset since, CancellationToken cancellationToken) =>
        db.Posts.CountAsync(p => p.AuthorId == authorId && p.CreatedAt >= since, cancellationToken);

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
            .Where(c => postIds.Contains(c.PostId) && c.Status == CommentStatus.Visible && !c.IsAuthorDeactivated)
            .GroupBy(c => c.PostId)
            .Select(g => new { PostId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Shares count once per viewer per UK day, like the author's reach.
        var shares = await db.PostShares
            .Where(s => postIds.Contains(s.PostId))
            .GroupBy(s => s.PostId)
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
                mine.TryGetValue(id, out var type) ? type : null,
                shares.FirstOrDefault(s => s.PostId == id)?.Count ?? 0));
    }

    public async Task SetAuthorDeactivatedAsync(Guid userId, bool deactivated, CancellationToken cancellationToken)
    {
        await db.Posts.Where(p => p.AuthorId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsAuthorDeactivated, deactivated), cancellationToken);
        await db.Comments.Where(c => c.AuthorId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsAuthorDeactivated, deactivated), cancellationToken);
    }

    public Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var commentIds = db.Comments.Where(c => c.AuthorId == userId).Select(c => c.Id);
            await db.Reports
                .Where(r => r.ReporterId == userId
                    || (r.TargetType == ReportTargetType.Comment && commentIds.Contains(r.TargetId))
                    || (r.TargetType == ReportTargetType.Profile && r.TargetId == userId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.Appeals.Where(a => a.AppellantId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.ReelPlays.Where(p => p.ViewerKey == userId && !p.IsAnonymous).ExecuteDeleteAsync(cancellationToken);
            await db.Reactions.Where(r => r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.PostVotes.Where(v => v.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.PostFollows.Where(f => f.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.Bookmarks.Where(b => b.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.PostViews.Where(v => v.ViewerKey == userId).ExecuteDeleteAsync(cancellationToken);
            await db.PostShares.Where(v => v.ViewerKey == userId).ExecuteDeleteAsync(cancellationToken);
            await db.AuthorPreferences.Where(a => a.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.CommentReactions.Where(r => r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.Comments.Where(c => c.AuthorId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.PostPassengers.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);

            // The database cascades the comment deletes to their reactions, and this to the posts' media links,
            // comments (and their reactions), reactions, reports and appeals. Other people's replies to the user's
            // comments stay, under a "comment removed" placeholder.
            await db.Posts.Where(p => p.AuthorId == userId).ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });

    public void Add(Post post) => db.Posts.Add(post);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public async Task<IReadOnlyList<PostPassenger>> GetPassengersAsync(
        IReadOnlyCollection<Guid> postIds,
        CancellationToken cancellationToken) =>
        postIds.Count == 0
            ? []
            : await db.PostPassengers.Where(p => postIds.Contains(p.PostId)).ToListAsync(cancellationToken);

    public void AddPassenger(PostPassenger passenger) => db.PostPassengers.Add(passenger);

    public void RemovePassenger(PostPassenger passenger) => db.PostPassengers.Remove(passenger);

    public async Task<(IReadOnlyList<Post> Items, int TotalCount)> ListTaggedAsync(
        Guid userId,
        PassengerTagStatus status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query =
            from tag in db.PostPassengers
            where tag.UserId == userId && tag.Status == status
            join post in db.Posts on tag.PostId equals post.Id
            where post.Status == PostStatus.Published && post.MediaState == PostMediaState.Ready && !post.IsAuthorDeactivated
            orderby tag.TaggedAt descending
            select post;
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Include(p => p.Media)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }
}
