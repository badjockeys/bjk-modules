using BJK.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;
using BJK.Modules.Queues;

namespace BJK.Modules.Media.Infrastructure.Data;

public interface IMediaRepository
{
    /// <summary>Any media except deleted.</summary>
    Task<MediaItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>A user's media (not deleted), newest first.</summary>
    Task<(IReadOnlyList<MediaItem> Items, int TotalCount)> ListByUserAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Media in one state, oldest first (e.g. the review queue).</summary>
    Task<(IReadOnlyList<MediaItem> Items, int TotalCount)> ListByStatusAsync(
        MediaStatus status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Several media by id, except deleted. Tracked, so changes can be saved.</summary>
    Task<IReadOnlyList<MediaItem>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Several media by id, except deleted, read fresh from the database and not tracked (read-only).</summary>
    Task<IReadOnlyList<MediaItem>> GetUntrackedAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Permanently removes every media row of a user, including deleted ones.</summary>
    Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The media's status and revision as saved now (not tracked), or null if it's gone.</summary>
    Task<(MediaStatus Status, int Revision)?> GetSavedStateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>True if the media is marked sensitive (it's in a post tagged sensitive). Always read from the database.</summary>
    Task<bool> IsSensitiveAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Which of the given media are marked sensitive.</summary>
    Task<IReadOnlySet<Guid>> GetSensitiveIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The sensitive marks of the given media (not tracked), by media id; unmarked media are left out.</summary>
    Task<IReadOnlyDictionary<Guid, SensitiveMediaMark>> GetSensitiveMarksAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the media sensitive, or clears the mark, straight away in the database (not through
    /// <see cref="SaveChangesAsync"/>, and without touching the media row, so it never clashes with a worker processing
    /// the media). Returns true if anything changed; safe to repeat and to run at the same time as another call. A
    /// post mark leaves an existing mark as it is; an automatic mark replaces a post mark (with the score). Clearing
    /// removes only post marks unless <paramref name="clearAutomatic"/> is true.
    /// </summary>
    Task<bool> SetSensitiveAsync(
        Guid id,
        bool isSensitive,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        SensitiveMarkSource source = SensitiveMarkSource.Post,
        double? score = null,
        bool clearAutomatic = false);

    /// <summary>
    /// Media whose original should be deleted now (tracked), oldest first: Ready or Failed media past
    /// <see cref="MediaItem.OriginalDeletesAt"/>, and uploads never completed that were started before
    /// <paramref name="abandonedBefore"/>. Never media still processing or waiting for review.
    /// </summary>
    Task<IReadOnlyList<MediaItem>> ListOriginalsToDeleteAsync(DateTimeOffset now, DateTimeOffset abandonedBefore, int max, CancellationToken cancellationToken);

    /// <summary>
    /// Media uploaded before <paramref name="uploadedBefore"/> that the unused-uploads sweep hasn't found in use yet:
    /// Ready, Failed or never finished uploading (not processing, not waiting for a moderator, not deleted), oldest
    /// first, at most <paramref name="max"/>.
    /// </summary>
    Task<IReadOnlyList<MediaItem>> ListUnusedCandidatesAsync(DateTimeOffset uploadedBefore, int max, CancellationToken cancellationToken);

    /// <summary>Reloads a tracked media item from the database, after someone else changed it.</summary>
    Task ReloadAsync(MediaItem media, CancellationToken cancellationToken);

    void Add(MediaItem media);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves the changes and whatever <paramref name="queueMessages"/> queues (e.g. a processing job) in one database
    /// transaction: both are saved, or neither.
    /// </summary>
    /// <param name="queueMessages">Queues the messages, e.g. through <see cref="BJK.Modules.Media.Infrastructure.Messaging.IMediaJobQueue"/>.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task SaveChangesWithMessagesAsync(Func<CancellationToken, Task> queueMessages, CancellationToken cancellationToken);
}

internal sealed class MediaRepository(MediaDbContext db) : IMediaRepository
{
    public Task<MediaItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Media.FirstOrDefaultAsync(m => m.Id == id && m.Status != MediaStatus.Deleted, cancellationToken);

    public async Task<(IReadOnlyList<MediaItem> Items, int TotalCount)> ListByUserAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Media.AsNoTracking().Where(m => m.UserId == userId && m.Status != MediaStatus.Deleted);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<(IReadOnlyList<MediaItem> Items, int TotalCount)> ListByStatusAsync(
        MediaStatus status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Media.AsNoTracking().Where(m => m.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyList<MediaItem>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken) =>
        await db.Media
            .Where(m => ids.Contains(m.Id) && m.Status != MediaStatus.Deleted)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MediaItem>> GetUntrackedAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken) =>
        await db.Media.AsNoTracking()
            .Where(m => ids.Contains(m.Id) && m.Status != MediaStatus.Deleted)
            .ToListAsync(cancellationToken);

    public async Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await db.Media.Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<(MediaStatus Status, int Revision)?> GetSavedStateAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Media.AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new { m.Status, m.Revision })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.Status, row.Revision);
    }

    public Task<bool> IsSensitiveAsync(Guid id, CancellationToken cancellationToken) =>
        db.SensitiveMedia.AsNoTracking().AnyAsync(m => m.MediaId == id, cancellationToken);

    public async Task<IReadOnlySet<Guid>> GetSensitiveIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        (await GetSensitiveMarksAsync(ids, cancellationToken)).Keys.ToHashSet();

    public async Task<IReadOnlyDictionary<Guid, SensitiveMediaMark>> GetSensitiveMarksAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, SensitiveMediaMark>();
        }

        return await db.SensitiveMedia.AsNoTracking()
            .Where(m => ids.Contains(m.MediaId))
            .ToDictionaryAsync(m => m.MediaId, cancellationToken);
    }

    public async Task<bool> SetSensitiveAsync(
        Guid id,
        bool isSensitive,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        SensitiveMarkSource source = SensitiveMarkSource.Post,
        double? score = null,
        bool clearAutomatic = false)
    {
        if (!isSensitive)
        {
            return await db.SensitiveMedia
                .Where(m => m.MediaId == id && (clearAutomatic || m.Source == SensitiveMarkSource.Post))
                .ExecuteDeleteAsync(cancellationToken) > 0;
        }

        if (source == SensitiveMarkSource.Automatic
            && await db.SensitiveMedia
                .Where(m => m.MediaId == id && m.Source != SensitiveMarkSource.Automatic)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.Source, SensitiveMarkSource.Automatic).SetProperty(m => m.Score, score), cancellationToken) > 0)
        {
            // Already marked through its post: now marked automatically too, so the author can't remove it.
            return false;
        }

        // Insert only if it isn't there; the locks make two calls at once safe (one inserts, the other does nothing).
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [media].[SensitiveMedia] ([MediaId], [MarkedAt], [Source], [Score])
            SELECT {id}, {now}, {source.ToString()}, {score}
            WHERE NOT EXISTS (SELECT 1 FROM [media].[SensitiveMedia] WITH (UPDLOCK, HOLDLOCK) WHERE [MediaId] = {id})
            """,
            cancellationToken);
        return inserted > 0;
    }

    public async Task<IReadOnlyList<MediaItem>> ListOriginalsToDeleteAsync(
        DateTimeOffset now,
        DateTimeOffset abandonedBefore,
        int max,
        CancellationToken cancellationToken) =>
        await db.Media
            .Where(m => m.OriginalDeletedAt == null
                && (((m.Status == MediaStatus.Ready || m.Status == MediaStatus.Failed) && m.OriginalDeletesAt != null && m.OriginalDeletesAt <= now)
                    || (m.Status == MediaStatus.Uploading && m.CreatedAt <= abandonedBefore)))
            .OrderBy(m => m.CreatedAt)
            .Take(max)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MediaItem>> ListUnusedCandidatesAsync(
        DateTimeOffset uploadedBefore,
        int max,
        CancellationToken cancellationToken) =>
        await db.Media
            .Where(m => m.UsageCheckedAt == null
                && m.Status != MediaStatus.Deleted
                && (m.Status == MediaStatus.Ready || m.Status == MediaStatus.Failed || m.Status == MediaStatus.Uploading)
                && m.CreatedAt <= uploadedBefore)
            .OrderBy(m => m.CreatedAt)
            .Take(max)
            .ToListAsync(cancellationToken);

    public Task ReloadAsync(MediaItem media, CancellationToken cancellationToken) =>
        db.Entry(media).ReloadAsync(cancellationToken);

    public void Add(MediaItem media) => db.Media.Add(media);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public Task SaveChangesWithMessagesAsync(Func<CancellationToken, Task> queueMessages, CancellationToken cancellationToken) =>
        db.SaveChangesWithMessagesAsync(queueMessages, cancellationToken);
}
