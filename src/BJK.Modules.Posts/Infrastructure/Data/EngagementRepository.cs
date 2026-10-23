using BJK.Modules.Posts.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>Vote counts on one post, and the viewer's own answer.</summary>
/// <param name="Counts">Votes per answer (answers nobody chose are left out).</param>
/// <param name="Mine">The viewer's answer, or null.</param>
public sealed record VoteTally(IReadOnlyDictionary<VoteChoice, int> Counts, VoteChoice? Mine)
{
    public static readonly VoteTally Empty = new(new Dictionary<VoteChoice, int>(), null);

    public int Total => Counts.Values.Sum();
}

/// <summary>How far a post reached, for its author: unique viewers, local ones, and shares.</summary>
public sealed record PostReach(int Views, int LocalViews, int Shares);

/// <summary>
/// When each thing happened to a post, for its analytics: views (and whether each was local), reactions, visible
/// comments and replies, and shares.
/// </summary>
public sealed record PostActivity(
    IReadOnlyList<(DateTimeOffset At, bool IsLocal)> Views,
    IReadOnlyList<DateTimeOffset> Reactions,
    IReadOnlyList<DateTimeOffset> Comments,
    IReadOnlyList<DateTimeOffset> Shares);

/// <summary>A viewer having seen a post, to record.</summary>
public sealed record ViewToRecord(Guid PostId, Guid ViewerKey, bool IsLocal);

/// <summary>Votes, follows, bookmarks, views, shares and authors' post settings.</summary>
public interface IEngagementRepository
{
    Task<PostVote?> GetVoteAsync(Guid postId, Guid userId, CancellationToken cancellationToken);

    void AddVote(PostVote vote);

    /// <summary>Vote counts per post, and the viewer's answers.</summary>
    Task<IReadOnlyDictionary<Guid, VoteTally>> GetVoteTalliesAsync(
        IReadOnlyCollection<Guid> postIds,
        Guid? viewerId,
        CancellationToken cancellationToken);

    Task<PostFollow?> GetFollowAsync(Guid postId, Guid userId, CancellationToken cancellationToken);

    void AddFollow(PostFollow follow);

    void RemoveFollow(PostFollow follow);

    /// <summary>Follows the post unless already following. Saves straight away; safe if two calls race.</summary>
    Task EnsureFollowingAsync(Guid postId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Who follows the post, oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<Guid>> ListFollowerIdsAsync(Guid postId, int max, CancellationToken cancellationToken);

    Task<Bookmark?> GetBookmarkAsync(Guid userId, Guid postId, CancellationToken cancellationToken);

    void AddBookmark(Bookmark bookmark);

    void RemoveBookmark(Bookmark bookmark);

    /// <summary>Of these posts, the ones the user follows and the ones they saved.</summary>
    Task<(IReadOnlySet<Guid> Following, IReadOnlySet<Guid> Saved)> GetViewerStateAsync(
        IReadOnlyCollection<Guid> postIds,
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>The user's saved posts that are still public (or their own), newest bookmark first.</summary>
    Task<(IReadOnlyList<Post> Items, int TotalCount)> ListBookmarkedAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Records views, ignoring any already counted. Returns how many were new.</summary>
    Task<int> RecordViewsAsync(IReadOnlyCollection<ViewToRecord> views, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records a share, once per viewer per UK day. Returns false if already counted today.</summary>
    Task<bool> RecordShareAsync(PostShare share, CancellationToken cancellationToken);

    Task<PostReach> GetReachAsync(Guid postId, CancellationToken cancellationToken);

    /// <summary>When each view, reaction, visible comment and share of a post happened.</summary>
    Task<PostActivity> GetActivityAsync(Guid postId, CancellationToken cancellationToken);

    Task<AuthorPreferences?> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken);

    void AddPreferences(AuthorPreferences preferences);

    /// <summary>Public posts whose vote has closed but whose result hasn't been announced, oldest first (tracked).</summary>
    Task<IReadOnlyList<Post>> ListVotesToAnnounceAsync(DateTimeOffset now, int max, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class EngagementRepository(PostsDbContext db) : IEngagementRepository
{
    private const int UniqueKeyViolation = 2627;
    private const int DuplicateKey = 2601;

    public Task<PostVote?> GetVoteAsync(Guid postId, Guid userId, CancellationToken cancellationToken) =>
        db.PostVotes.FirstOrDefaultAsync(v => v.PostId == postId && v.UserId == userId, cancellationToken);

    public void AddVote(PostVote vote) => db.PostVotes.Add(vote);

    public async Task<IReadOnlyDictionary<Guid, VoteTally>> GetVoteTalliesAsync(
        IReadOnlyCollection<Guid> postIds,
        Guid? viewerId,
        CancellationToken cancellationToken)
    {
        if (postIds.Count == 0)
        {
            return new Dictionary<Guid, VoteTally>();
        }

        var counts = await db.PostVotes.AsNoTracking()
            .Where(v => postIds.Contains(v.PostId))
            .GroupBy(v => new { v.PostId, v.Choice })
            .Select(g => new { g.Key.PostId, g.Key.Choice, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var mine = viewerId is { } userId
            ? await db.PostVotes.AsNoTracking()
                .Where(v => v.UserId == userId && postIds.Contains(v.PostId))
                .ToDictionaryAsync(v => v.PostId, v => v.Choice, cancellationToken)
            : new Dictionary<Guid, VoteChoice>();

        return postIds.Distinct().ToDictionary(
            id => id,
            id => new VoteTally(
                counts.Where(c => c.PostId == id).ToDictionary(c => c.Choice, c => c.Count),
                mine.TryGetValue(id, out var choice) ? choice : null));
    }

    public Task<PostFollow?> GetFollowAsync(Guid postId, Guid userId, CancellationToken cancellationToken) =>
        db.PostFollows.FirstOrDefaultAsync(f => f.PostId == postId && f.UserId == userId, cancellationToken);

    public void AddFollow(PostFollow follow) => db.PostFollows.Add(follow);

    public void RemoveFollow(PostFollow follow) => db.PostFollows.Remove(follow);

    public async Task EnsureFollowingAsync(Guid postId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO [posts].[PostFollows] ([PostId], [UserId], [CreatedAt])
                SELECT {postId}, {userId}, {now}
                WHERE NOT EXISTS (SELECT 1 FROM [posts].[PostFollows] WHERE [PostId] = {postId} AND [UserId] = {userId});
                """,
                cancellationToken);
        }
        catch (SqlException ex) when (ex.Number is UniqueKeyViolation or DuplicateKey)
        {
            // Followed by a call running at the same moment: the result is the same.
        }
    }

    public async Task<IReadOnlyList<Guid>> ListFollowerIdsAsync(Guid postId, int max, CancellationToken cancellationToken) =>
        await db.PostFollows.AsNoTracking()
            .Where(f => f.PostId == postId)
            .OrderBy(f => f.CreatedAt)
            .Select(f => f.UserId)
            .Take(max)
            .ToListAsync(cancellationToken);

    public Task<Bookmark?> GetBookmarkAsync(Guid userId, Guid postId, CancellationToken cancellationToken) =>
        db.Bookmarks.FirstOrDefaultAsync(b => b.UserId == userId && b.PostId == postId, cancellationToken);

    public void AddBookmark(Bookmark bookmark) => db.Bookmarks.Add(bookmark);

    public void RemoveBookmark(Bookmark bookmark) => db.Bookmarks.Remove(bookmark);

    public async Task<(IReadOnlySet<Guid> Following, IReadOnlySet<Guid> Saved)> GetViewerStateAsync(
        IReadOnlyCollection<Guid> postIds,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (postIds.Count == 0)
        {
            return (new HashSet<Guid>(), new HashSet<Guid>());
        }

        var following = await db.PostFollows.AsNoTracking()
            .Where(f => f.UserId == userId && postIds.Contains(f.PostId))
            .Select(f => f.PostId)
            .ToListAsync(cancellationToken);
        var saved = await db.Bookmarks.AsNoTracking()
            .Where(b => b.UserId == userId && postIds.Contains(b.PostId))
            .Select(b => b.PostId)
            .ToListAsync(cancellationToken);
        return (following.ToHashSet(), saved.ToHashSet());
    }

    public async Task<(IReadOnlyList<Post> Items, int TotalCount)> ListBookmarkedAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query =
            from b in db.Bookmarks.AsNoTracking()
            join p in db.Posts.AsNoTracking() on b.PostId equals p.Id
            where b.UserId == userId
                && p.Status != PostStatus.Deleted
                && (p.AuthorId == userId
                    || (p.Status == PostStatus.Published && p.MediaState == PostMediaState.Ready && !p.IsAuthorDeactivated))
            select new { Bookmark = b, Post = p };
        var total = await query.CountAsync(cancellationToken);
        var ids = await query
            .OrderByDescending(x => x.Bookmark.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.Post.Id)
            .ToListAsync(cancellationToken);
        var posts = await db.Posts.AsNoTracking()
            .Include(p => p.Media)
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(cancellationToken);
        return (ids.Select(id => posts.First(p => p.Id == id)).ToList(), total);
    }

    public async Task<int> RecordViewsAsync(IReadOnlyCollection<ViewToRecord> views, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var added = 0;
        foreach (var view in views.DistinctBy(v => (v.PostId, v.ViewerKey)))
        {
            try
            {
                added += await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO [posts].[PostViews] ([PostId], [ViewerKey], [IsLocal], [ViewedAt])
                    SELECT {view.PostId}, {view.ViewerKey}, {view.IsLocal}, {now}
                    WHERE NOT EXISTS (SELECT 1 FROM [posts].[PostViews] WHERE [PostId] = {view.PostId} AND [ViewerKey] = {view.ViewerKey})
                      AND EXISTS (SELECT 1 FROM [posts].[Posts] WHERE [Id] = {view.PostId});
                    """,
                    cancellationToken);
            }
            catch (SqlException ex) when (ex.Number is UniqueKeyViolation or DuplicateKey)
            {
                // Counted by a request running at the same moment.
            }
        }

        return added;
    }

    public async Task<bool> RecordShareAsync(PostShare share, CancellationToken cancellationToken)
    {
        try
        {
            return await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO [posts].[PostShares] ([PostId], [ViewerKey], [Day], [SharedAt])
                SELECT {share.PostId}, {share.ViewerKey}, {share.Day}, {share.SharedAt}
                WHERE NOT EXISTS (
                    SELECT 1 FROM [posts].[PostShares]
                    WHERE [PostId] = {share.PostId} AND [ViewerKey] = {share.ViewerKey} AND [Day] = {share.Day});
                """,
                cancellationToken) == 1;
        }
        catch (SqlException ex) when (ex.Number is UniqueKeyViolation or DuplicateKey)
        {
            return false;
        }
    }

    public async Task<PostReach> GetReachAsync(Guid postId, CancellationToken cancellationToken)
    {
        var views = await db.PostViews.AsNoTracking()
            .Where(v => v.PostId == postId)
            .GroupBy(v => v.IsLocal)
            .Select(g => new { IsLocal = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var shares = await db.PostShares.CountAsync(s => s.PostId == postId, cancellationToken);
        return new PostReach(views.Sum(v => v.Count), views.Where(v => v.IsLocal).Sum(v => v.Count), shares);
    }

    public async Task<PostActivity> GetActivityAsync(Guid postId, CancellationToken cancellationToken)
    {
        var views = await db.PostViews.AsNoTracking().Where(v => v.PostId == postId)
            .Select(v => new { v.ViewedAt, v.IsLocal }).ToListAsync(cancellationToken);
        var reactions = await db.Reactions.AsNoTracking().Where(r => r.PostId == postId)
            .Select(r => r.CreatedAt).ToListAsync(cancellationToken);
        var comments = await db.Comments.AsNoTracking().Where(c => c.PostId == postId && c.Status == CommentStatus.Visible)
            .Select(c => c.CreatedAt).ToListAsync(cancellationToken);
        var shares = await db.PostShares.AsNoTracking().Where(s => s.PostId == postId)
            .Select(s => s.SharedAt).ToListAsync(cancellationToken);
        return new PostActivity(views.Select(v => (v.ViewedAt, v.IsLocal)).ToList(), reactions, comments, shares);
    }

    public Task<AuthorPreferences?> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken) =>
        db.AuthorPreferences.FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);

    public void AddPreferences(AuthorPreferences preferences) => db.AuthorPreferences.Add(preferences);

    public async Task<IReadOnlyList<Post>> ListVotesToAnnounceAsync(DateTimeOffset now, int max, CancellationToken cancellationToken) =>
        await db.Posts
            .Where(p => p.VoteClosesAt != null && p.VoteResultSentAt == null && p.VoteClosesAt <= now)
            .OrderBy(p => p.VoteClosesAt)
            .Take(max)
            .ToListAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
