using BJK.Modules.Media.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Media.Infrastructure.Data;

/// <summary>What a worker found while blurring one segment.</summary>
/// <param name="Frames">Frames written.</param>
/// <param name="FacesBlurred">Faces followed through the segment's own part.</param>
/// <param name="PlatesBlurred">Plates followed through the segment's own part.</param>
/// <param name="ReviewReasons">Why a moderator should check it; empty if none.</param>
/// <param name="Elapsed">Time taken.</param>
public sealed record VideoSegmentResult(int Frames, int FacesBlurred, int PlatesBlurred, IReadOnlyList<string> ReviewReasons, TimeSpan Elapsed);

/// <summary>
/// Progress of videos processed in segments (<c>media.VideoJobs</c>, <c>media.VideoSegments</c>). Every change that
/// several workers might make at once is a single conditional update in the database, so repeated or simultaneous
/// deliveries of the same job can't double-count or lose work.
/// </summary>
public interface IVideoJobStore
{
    /// <summary>The job for a media revision, with its segments (tracked, so it can be saved with the media), or null.</summary>
    Task<VideoJob?> GetAsync(Guid mediaId, int revision, CancellationToken cancellationToken);

    /// <summary>Saves a new job and its segments. False if one already exists for that media revision (nothing changes).</summary>
    Task<bool> TryCreateAsync(VideoJob job, CancellationToken cancellationToken);

    /// <summary>Counts a worker starting on a segment.</summary>
    Task RecordAttemptAsync(Guid mediaId, int revision, int index, CancellationToken cancellationToken);

    /// <summary>Marks a segment done with its results. False if it was already done (by an earlier or parallel delivery).</summary>
    Task<bool> TryCompleteAsync(Guid mediaId, int revision, int index, VideoSegmentResult result, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>True when every segment of the job is done.</summary>
    Task<bool> AllDoneAsync(Guid mediaId, int revision, CancellationToken cancellationToken);

    /// <summary>
    /// Records that the assemble job is being queued, if every segment is done and nobody has queued it yet. True for
    /// exactly one caller.
    /// </summary>
    Task<bool> TryClaimAssemblyAsync(Guid mediaId, int revision, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class VideoJobStore(MediaDbContext db) : IVideoJobStore
{
    public Task<VideoJob?> GetAsync(Guid mediaId, int revision, CancellationToken cancellationToken) =>
        db.VideoJobs
            .Include(j => j.Segments)
            .FirstOrDefaultAsync(j => j.MediaId == mediaId && j.Revision == revision, cancellationToken);

    public async Task<bool> TryCreateAsync(VideoJob job, CancellationToken cancellationToken)
    {
        if (await db.VideoJobs.AnyAsync(j => j.MediaId == job.MediaId && j.Revision == job.Revision, cancellationToken))
        {
            return false;
        }

        db.VideoJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            // Another delivery of the same job created it a moment ago.
            db.Entry(job).State = EntityState.Detached;
            foreach (var segment in job.Segments)
            {
                db.Entry(segment).State = EntityState.Detached;
            }

            return false;
        }
    }

    public Task RecordAttemptAsync(Guid mediaId, int revision, int index, CancellationToken cancellationToken) =>
        db.VideoSegments
            .Where(s => s.MediaId == mediaId && s.Revision == revision && s.Index == index)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Attempts, s => s.Attempts + 1), cancellationToken);

    public async Task<bool> TryCompleteAsync(
        Guid mediaId,
        int revision,
        int index,
        VideoSegmentResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reasons = VideoSegment.JoinReasons(result.ReviewReasons);
        var elapsed = (long)result.Elapsed.TotalMilliseconds;
        var updated = await db.VideoSegments
            .Where(s => s.MediaId == mediaId && s.Revision == revision && s.Index == index && s.Status == VideoSegmentStatus.Pending)
            .ExecuteUpdateAsync(
                u => u
                    .SetProperty(s => s.Status, VideoSegmentStatus.Done)
                    .SetProperty(s => s.Frames, result.Frames)
                    .SetProperty(s => s.FacesBlurred, result.FacesBlurred)
                    .SetProperty(s => s.PlatesBlurred, result.PlatesBlurred)
                    .SetProperty(s => s.ReviewReasons, reasons)
                    .SetProperty(s => s.ElapsedMilliseconds, elapsed)
                    .SetProperty(s => s.CompletedAt, now),
                cancellationToken);
        return updated == 1;
    }

    public Task<bool> AllDoneAsync(Guid mediaId, int revision, CancellationToken cancellationToken) =>
        db.VideoJobs.AnyAsync(
            j => j.MediaId == mediaId
                && j.Revision == revision
                && !db.VideoSegments.Any(s => s.MediaId == mediaId && s.Revision == revision && s.Status != VideoSegmentStatus.Done),
            cancellationToken);

    public async Task<bool> TryClaimAssemblyAsync(Guid mediaId, int revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // One statement: only the first caller to see every segment done gets a row back.
        var updated = await db.VideoJobs
            .Where(j => j.MediaId == mediaId
                && j.Revision == revision
                && j.AssembleQueuedAt == null
                && !db.VideoSegments.Any(s => s.MediaId == mediaId && s.Revision == revision && s.Status != VideoSegmentStatus.Done))
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.AssembleQueuedAt, now), cancellationToken);
        return updated == 1;
    }
}
