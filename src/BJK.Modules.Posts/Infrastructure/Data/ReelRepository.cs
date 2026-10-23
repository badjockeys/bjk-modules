using System.Data;
using BJK.Modules.Posts.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>Where a page of reels starts: after this score and creation time (reels are ranked by both, highest first).</summary>
/// <param name="Score">The last reel's <see cref="Post.ReelScore"/>.</param>
/// <param name="CreatedAt">The last reel's creation time.</param>
public sealed record ReelCursor(double Score, DateTimeOffset CreatedAt);

/// <summary>Which reels to list.</summary>
/// <param name="Category">Only posts with this subcategory of bad driving (among their subcategories).</param>
/// <param name="IncludeSensitive">Include sensitive posts (age-verified viewers and moderators).</param>
/// <param name="ViewerId">The viewer, if signed in: their own sensitive posts are always included.</param>
/// <param name="ExcludeAuthorIds">Leave out these authors (blocked either way).</param>
/// <param name="After">Continue after this reel.</param>
/// <param name="Group">Only posts with a subcategory in this group.</param>
public sealed record ReelSearch(
    IncidentCategory? Category,
    bool IncludeSensitive,
    Guid? ViewerId,
    IReadOnlyCollection<Guid>? ExcludeAuthorIds,
    ReelCursor? After,
    IncidentGroup? Group = null);

public interface IReelRepository
{
    /// <summary>Public video posts, best ranked first, with their media.</summary>
    Task<IReadOnlyList<Post>> ListAsync(ReelSearch search, int take, CancellationToken cancellationToken);

    /// <summary>How many reels the viewer has played on the day.</summary>
    Task<int> CountPlaysAsync(Guid viewerKey, DateOnly day, CancellationToken cancellationToken);

    /// <summary>True if the viewer already played this post on the day.</summary>
    Task<bool> HasPlayedAsync(Guid viewerKey, DateOnly day, Guid postId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a play and adds it to the post's count for the day, in one transaction. Returns false (and changes
    /// nothing) if the viewer already played the post that day.
    /// </summary>
    Task<bool> RecordPlayAsync(ReelPlay play, CancellationToken cancellationToken);

    /// <summary>
    /// Recomputes <see cref="Post.ReelScore"/> for video posts created since <paramref name="windowStart"/>, and sets it
    /// to 0 for older ones. Returns the number of posts scored.
    /// </summary>
    Task<int> RefreshScoresAsync(DateTimeOffset now, DateTimeOffset windowStart, CancellationToken cancellationToken);

    /// <summary>Deletes signed-in viewers' plays before one day and logged-out viewers' before another.</summary>
    Task<int> DeletePlaysBeforeAsync(DateOnly userCutoff, DateOnly anonymousCutoff, CancellationToken cancellationToken);
}

internal sealed class ReelRepository(PostsDbContext db) : IReelRepository
{
    private const int UniqueKeyViolation = 2627;
    private const int DuplicateKey = 2601;

    public async Task<IReadOnlyList<Post>> ListAsync(ReelSearch search, int take, CancellationToken cancellationToken)
    {
        // The same conditions as the filtered indexes IX_Posts_Reels and IX_Posts_Reels_Category.
        var query = db.Posts.AsNoTracking().Where(p =>
            p.HasVideo
            && p.Status == PostStatus.Published
            && p.MediaState == PostMediaState.Ready
            && !p.IsAuthorDeactivated);

        query = query.InCategory(search.Group, search.Category);

        if (!search.IncludeSensitive)
        {
            query = search.ViewerId is { } viewerId
                ? query.Where(p => !p.IsSensitive || p.AuthorId == viewerId)
                : query.Where(p => !p.IsSensitive);
        }

        if (search.ExcludeAuthorIds is { Count: > 0 } excluded)
        {
            query = query.Where(p => !excluded.Contains(p.AuthorId));
        }

        if (search.After is { } after)
        {
            query = query.Where(p => p.ReelScore < after.Score || (p.ReelScore == after.Score && p.CreatedAt < after.CreatedAt));
        }

        return await query
            .OrderByDescending(p => p.ReelScore)
            .ThenByDescending(p => p.CreatedAt)
            .Take(take)
            .Include(p => p.Media)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountPlaysAsync(Guid viewerKey, DateOnly day, CancellationToken cancellationToken) =>
        db.ReelPlays.CountAsync(p => p.ViewerKey == viewerKey && p.Day == day, cancellationToken);

    public Task<bool> HasPlayedAsync(Guid viewerKey, DateOnly day, Guid postId, CancellationToken cancellationToken) =>
        db.ReelPlays.AnyAsync(p => p.ViewerKey == viewerKey && p.Day == day && p.PostId == postId, cancellationToken);

    public Task<bool> RecordPlayAsync(ReelPlay play, CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            int inserted;
            try
            {
                // UPDLOCK + HOLDLOCK: two reports of the same play at once queue up here instead of both inserting.
                inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO [posts].[ReelPlays] ([ViewerKey], [Day], [PostId], [IsAnonymous], [PlayedAt])
                    SELECT {play.ViewerKey}, {play.Day}, {play.PostId}, {play.IsAnonymous}, {play.PlayedAt}
                    WHERE NOT EXISTS (
                        SELECT 1 FROM [posts].[ReelPlays] WITH (UPDLOCK, HOLDLOCK)
                        WHERE [ViewerKey] = {play.ViewerKey} AND [Day] = {play.Day} AND [PostId] = {play.PostId});
                    """,
                    cancellationToken);
            }
            catch (SqlException ex) when (ex.Number is UniqueKeyViolation or DuplicateKey)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            if (inserted == 1)
            {
                var updated = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE [posts].[ReelPlayDailyCounts] WITH (UPDLOCK, HOLDLOCK)
                    SET [Plays] = [Plays] + 1
                    WHERE [PostId] = {play.PostId} AND [Day] = {play.Day};
                    """,
                    cancellationToken);
                if (updated == 0)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO [posts].[ReelPlayDailyCounts] ([PostId], [Day], [Plays])
                        VALUES ({play.PostId}, {play.Day}, 1);
                        """,
                        cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return inserted == 1;
        });

    public Task<int> RefreshScoresAsync(DateTimeOffset now, DateTimeOffset windowStart, CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // ReelRanking.Score, set-based: (1 + reactions + 2 × visible comments) / (age in hours + 2)^1.5. Only
            // posts inside the window, so the work stays proportional to recent uploads, not the whole archive.
            var scored = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE [p]
                SET [ReelScore] = (1.0 + [r].[Count] + 2.0 * [c].[Count])
                    / POWER(
                        CASE WHEN [p].[CreatedAt] > {now} THEN 0.0
                             ELSE CAST(DATEDIFF_BIG(second, [p].[CreatedAt], {now}) AS float) / 3600.0 END
                        + {ReelRanking.AgeOffsetHours},
                        {ReelRanking.Gravity})
                FROM [posts].[Posts] AS [p]
                CROSS APPLY (SELECT COUNT(*) AS [Count] FROM [posts].[Reactions] AS [x] WHERE [x].[PostId] = [p].[Id]) AS [r]
                CROSS APPLY (
                    SELECT COUNT(*) AS [Count] FROM [posts].[Comments] AS [x]
                    WHERE [x].[PostId] = [p].[Id] AND [x].[Status] = N'Visible' AND [x].[IsAuthorDeactivated] = 0) AS [c]
                WHERE [p].[Status] <> N'Deleted' AND [p].[CreatedAt] >= {windowStart};
                """,
                cancellationToken);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE [posts].[Posts] SET [ReelScore] = 0
                WHERE [CreatedAt] < {windowStart} AND [ReelScore] <> 0;
                """,
                cancellationToken);
            return scored;
        });

    public Task<int> DeletePlaysBeforeAsync(DateOnly userCutoff, DateOnly anonymousCutoff, CancellationToken cancellationToken) =>
        db.ReelPlays
            .Where(p => (!p.IsAnonymous && p.Day < userCutoff) || (p.IsAnonymous && p.Day < anonymousCutoff))
            .ExecuteDeleteAsync(cancellationToken);
}
