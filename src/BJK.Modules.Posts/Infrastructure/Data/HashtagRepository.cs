using System.Data;
using BJK.Modules.Posts.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>A trending tag or place: its public posts and distinct authors in the last 24 hours, and its usual level.</summary>
public sealed class TrendingRow
{
    public string Name { get; set; } = string.Empty;

    public int Posts { get; set; }

    public int Authors { get; set; }

    /// <summary>Posts over the baseline days before the last 24 hours.</summary>
    public int BaselinePosts { get; set; }
}

public interface IHashtagRepository
{
    /// <summary>The tags with these lower-case keys (tracked).</summary>
    Task<IReadOnlyList<Hashtag>> GetByKeysAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);

    /// <summary>The tags with these ids (tracked).</summary>
    Task<IReadOnlyList<Hashtag>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>One tag by its key, or null.</summary>
    Task<Hashtag?> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>A post's tag links (tracked).</summary>
    Task<IReadOnlyList<PostHashtag>> GetLinksAsync(Guid postId, CancellationToken cancellationToken);

    /// <summary>Tags starting with <paramref name="prefix"/> (lower case), most used first, not hidden from Trending.</summary>
    Task<IReadOnlyList<Hashtag>> SuggestAsync(string prefix, int limit, CancellationToken cancellationToken);

    void Add(Hashtag hashtag);

    void Add(PostHashtag link);

    void Remove(PostHashtag link);

    /// <summary>
    /// Rebuilds the hourly counts from <paramref name="since"/> (the start of an hour) from public posts only, and
    /// deletes counts older than <paramref name="deleteBefore"/>. One statement, in a transaction.
    /// </summary>
    Task RebuildHourlyCountsAsync(DateTimeOffset since, DateTimeOffset deleteBefore, CancellationToken cancellationToken);

    /// <summary>
    /// Tags with public posts since <paramref name="dayAgo"/>, not hidden from Trending, with at least the minimums,
    /// and their posts from <paramref name="baselineFrom"/> to <paramref name="dayAgo"/> (from the hourly counts),
    /// fastest growing first.
    /// </summary>
    Task<IReadOnlyList<TrendingRow>> TrendingTagsAsync(
        DateTimeOffset dayAgo,
        DateTimeOffset baselineFrom,
        int baselineDays,
        int minPosts,
        int minAuthors,
        int top,
        CancellationToken cancellationToken);

    /// <summary>The same for towns: the part of posts' place names after the last comma ("Prescot").</summary>
    Task<IReadOnlyList<TrendingRow>> TrendingPlacesAsync(
        DateTimeOffset dayAgo,
        DateTimeOffset baselineFrom,
        int baselineDays,
        int minPosts,
        int minAuthors,
        int top,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Hashtags and Trending, in SQL where it counts many rows.</summary>
internal sealed class HashtagRepository(PostsDbContext db) : IHashtagRepository
{
    // Posts' public filter, qualified for joins.
    private const string PublicPost = "[p].[Status] = N'Published' AND [p].[MediaState] = N'Ready' AND [p].[IsAuthorDeactivated] = 0";

    // The town in a place name: what follows the last comma, or all of it.
    private const string Town = "LTRIM(RIGHT([p].[PlaceName], CHARINDEX(',', REVERSE([p].[PlaceName]) + ',') - 1))";

    public async Task<IReadOnlyList<Hashtag>> GetByKeysAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        keys.Count == 0 ? [] : await db.Hashtags.Where(h => keys.Contains(h.Key)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Hashtag>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? [] : await db.Hashtags.Where(h => ids.Contains(h.Id)).ToListAsync(cancellationToken);

    public Task<Hashtag?> FindAsync(string key, CancellationToken cancellationToken) =>
        db.Hashtags.AsNoTracking().FirstOrDefaultAsync(h => h.Key == key, cancellationToken);

    public async Task<IReadOnlyList<PostHashtag>> GetLinksAsync(Guid postId, CancellationToken cancellationToken) =>
        await db.PostHashtags.Where(t => t.PostId == postId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Hashtag>> SuggestAsync(string prefix, int limit, CancellationToken cancellationToken) =>
        await db.Hashtags.AsNoTracking()
            .Where(h => h.Key.StartsWith(prefix) && !h.IsHiddenFromTrends && h.TotalUses > 0)
            .OrderByDescending(h => h.TotalUses)
            .ThenBy(h => h.Key)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(Hashtag hashtag) => db.Hashtags.Add(hashtag);

    public void Add(PostHashtag link) => db.PostHashtags.Add(link);

    public void Remove(PostHashtag link) => db.PostHashtags.Remove(link);

    public async Task RebuildHourlyCountsAsync(DateTimeOffset since, DateTimeOffset deleteBefore, CancellationToken cancellationToken)
    {
        const string Hour =
            "TODATETIMEOFFSET(DATEADD(hour, DATEDIFF(hour, CAST('20000101' AS datetime2), CAST(SWITCHOFFSET([t].[PostedAt], '+00:00') AS datetime2)), CAST('20000101' AS datetime2)), '+00:00')";
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DELETE FROM [posts].[HashtagHourlyCounts] WHERE [Hour] >= @since OR [Hour] < @deleteBefore;
            INSERT INTO [posts].[HashtagHourlyCounts] ([HashtagId], [Hour], [Posts], [Authors])
            SELECT [t].[HashtagId], {Hour}, COUNT(*), COUNT(DISTINCT [t].[AuthorId])
            FROM [posts].[PostHashtags] AS [t]
            JOIN [posts].[Posts] AS [p] ON [p].[Id] = [t].[PostId]
            WHERE [t].[PostedAt] >= @since AND {PublicPost}
            GROUP BY [t].[HashtagId], {Hour};
            COMMIT TRANSACTION;
            """;
        await db.Database.ExecuteSqlRawAsync(
            sql,
            [
                new SqlParameter("@since", SqlDbType.DateTimeOffset) { Value = since },
                new SqlParameter("@deleteBefore", SqlDbType.DateTimeOffset) { Value = deleteBefore },
            ],
            cancellationToken);
    }

    public async Task<IReadOnlyList<TrendingRow>> TrendingTagsAsync(
        DateTimeOffset dayAgo,
        DateTimeOffset baselineFrom,
        int baselineDays,
        int minPosts,
        int minAuthors,
        int top,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            WITH [recent] AS (
                SELECT [t].[HashtagId], COUNT(*) AS [Posts], COUNT(DISTINCT [t].[AuthorId]) AS [Authors]
                FROM [posts].[PostHashtags] AS [t]
                JOIN [posts].[Posts] AS [p] ON [p].[Id] = [t].[PostId]
                WHERE [t].[PostedAt] >= @dayAgo AND {PublicPost}
                GROUP BY [t].[HashtagId]),
            [usual] AS (
                SELECT [HashtagId], SUM([Posts]) AS [Posts]
                FROM [posts].[HashtagHourlyCounts]
                WHERE [Hour] >= @baselineFrom AND [Hour] < @dayAgo
                GROUP BY [HashtagId])
            SELECT TOP (@top) [h].[Display] AS [Name], [r].[Posts], [r].[Authors], COALESCE([u].[Posts], 0) AS [BaselinePosts]
            FROM [recent] AS [r]
            JOIN [posts].[Hashtags] AS [h] ON [h].[Id] = [r].[HashtagId]
            LEFT JOIN [usual] AS [u] ON [u].[HashtagId] = [r].[HashtagId]
            WHERE [h].[IsHiddenFromTrends] = 0 AND [r].[Posts] >= @minPosts AND [r].[Authors] >= @minAuthors
            ORDER BY [r].[Posts] * 1.0 / (COALESCE([u].[Posts], 0) * 1.0 / @baselineDays + 1) DESC, [r].[Posts] DESC, [h].[Key]
            """;
        return await db.Database.SqlQueryRaw<TrendingRow>(sql, Parameters(dayAgo, baselineFrom, baselineDays, minPosts, minAuthors, top))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TrendingRow>> TrendingPlacesAsync(
        DateTimeOffset dayAgo,
        DateTimeOffset baselineFrom,
        int baselineDays,
        int minPosts,
        int minAuthors,
        int top,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            WITH [recent] AS (
                SELECT {Town} AS [Name], COUNT(*) AS [Posts], COUNT(DISTINCT [p].[AuthorId]) AS [Authors]
                FROM [posts].[Posts] AS [p]
                WHERE [p].[CreatedAt] >= @dayAgo AND [p].[PlaceName] IS NOT NULL AND {PublicPost}
                GROUP BY {Town}),
            [usual] AS (
                SELECT {Town} AS [Name], COUNT(*) AS [Posts]
                FROM [posts].[Posts] AS [p]
                WHERE [p].[CreatedAt] >= @baselineFrom AND [p].[CreatedAt] < @dayAgo AND [p].[PlaceName] IS NOT NULL AND {PublicPost}
                GROUP BY {Town})
            SELECT TOP (@top) [r].[Name], [r].[Posts], [r].[Authors], COALESCE([u].[Posts], 0) AS [BaselinePosts]
            FROM [recent] AS [r]
            LEFT JOIN [usual] AS [u] ON [u].[Name] = [r].[Name]
            WHERE [r].[Name] <> N'' AND [r].[Posts] >= @minPosts AND [r].[Authors] >= @minAuthors
            ORDER BY [r].[Posts] * 1.0 / (COALESCE([u].[Posts], 0) * 1.0 / @baselineDays + 1) DESC, [r].[Posts] DESC, [r].[Name]
            """;
        return await db.Database.SqlQueryRaw<TrendingRow>(sql, Parameters(dayAgo, baselineFrom, baselineDays, minPosts, minAuthors, top))
            .ToListAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    private static object[] Parameters(DateTimeOffset dayAgo, DateTimeOffset baselineFrom, int baselineDays, int minPosts, int minAuthors, int top) =>
    [
        new SqlParameter("@dayAgo", SqlDbType.DateTimeOffset) { Value = dayAgo },
        new SqlParameter("@baselineFrom", SqlDbType.DateTimeOffset) { Value = baselineFrom },
        new SqlParameter("@baselineDays", SqlDbType.Int) { Value = baselineDays },
        new SqlParameter("@minPosts", SqlDbType.Int) { Value = minPosts },
        new SqlParameter("@minAuthors", SqlDbType.Int) { Value = minAuthors },
        new SqlParameter("@top", SqlDbType.Int) { Value = top },
    ];
}
