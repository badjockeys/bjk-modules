namespace BJK.Modules.Media.Domain;

/// <summary>
/// A longer video being processed in segments by several workers at once, for one <see cref="MediaItem.Revision"/>.
/// Stored in <c>media.VideoJobs</c>, with one <see cref="VideoSegment"/> per part in <c>media.VideoSegments</c>, so the
/// work survives worker restarts and repeated queue deliveries: a finished segment is never redone, and the final
/// "assemble" step runs once all of them are finished.
/// </summary>
public sealed class VideoJob
{
    private readonly List<VideoSegment> _segments = [];

    // For EF Core.
    private VideoJob()
    {
    }

    public Guid MediaId { get; private set; }

    /// <summary>The media's revision this job is for; a moderator's re-processing starts a new job.</summary>
    public int Revision { get; private set; }

    /// <summary>Frames per second of the output (the source's, at most 30).</summary>
    public double FrameRate { get; private set; }

    /// <summary>Frames in each segment except the last, which runs to the end of the video.</summary>
    public int FramesPerSegment { get; private set; }

    /// <summary>Frames decoded and analysed before and after each segment's own part.</summary>
    public int OverlapFrames { get; private set; }

    /// <summary>
    /// Where frame 0 sits in the source's timeline (round(start time x frame rate)), so every segment counts frames
    /// the same way.
    /// </summary>
    public long FirstFrame { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When a worker first queued the assemble job (to avoid queueing it twice in the normal case).</summary>
    public DateTimeOffset? AssembleQueuedAt { get; private set; }

    /// <summary>When the segments were joined and the media published or sent to review.</summary>
    public DateTimeOffset? AssembledAt { get; private set; }

    public IReadOnlyList<VideoSegment> Segments => _segments;

    public int SegmentCount => _segments.Count;

    /// <summary>Plans the segments for a video.</summary>
    /// <param name="mediaId">The media.</param>
    /// <param name="revision">Its current revision.</param>
    /// <param name="plan">How to split it (see <see cref="VideoSegmentPlan.For"/>).</param>
    /// <param name="now">The time.</param>
    public static VideoJob Create(Guid mediaId, int revision, VideoSegmentPlan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var job = new VideoJob
        {
            MediaId = mediaId,
            Revision = revision,
            FrameRate = plan.FrameRate,
            FramesPerSegment = plan.FramesPerSegment,
            OverlapFrames = plan.OverlapFrames,
            FirstFrame = plan.FirstFrame,
            CreatedAt = now,
        };
        for (var i = 0; i < plan.SegmentCount; i++)
        {
            job._segments.Add(new VideoSegment(mediaId, revision, i, plan.CoreStart(i), plan.CoreEnd(i)));
        }

        return job;
    }

    /// <summary>The plan this job was made from.</summary>
    public VideoSegmentPlan Plan => new(FrameRate, FramesPerSegment, OverlapFrames, FirstFrame, SegmentCount);

    /// <summary>The segments and the media have been finished. Saved with the media's new status.</summary>
    /// <param name="now">The time.</param>
    public void MarkAssembled(DateTimeOffset now) => AssembledAt ??= now;

    /// <summary>
    /// In-memory version of the store's claim: true (once) if every segment is done and nobody has queued the
    /// assemble job yet. The database store does the same in one conditional update.
    /// </summary>
    /// <param name="now">The time.</param>
    public bool TryClaimAssembly(DateTimeOffset now)
    {
        if (AssembleQueuedAt is not null || _segments.Any(s => !s.IsDone))
        {
            return false;
        }

        AssembleQueuedAt = now;
        return true;
    }
}

/// <summary>Whether a segment's blurred part has been made.</summary>
public enum VideoSegmentStatus
{
    Pending,

    /// <summary>Blurred, encoded and stored privately, waiting to be joined.</summary>
    Done,
}

/// <summary>One part of a <see cref="VideoJob"/>, processed as its own queue job.</summary>
public sealed class VideoSegment
{
    public const int ReviewReasonsMaxLength = 1000;

    // For EF Core.
    private VideoSegment()
    {
    }

    internal VideoSegment(Guid mediaId, int revision, int index, int startFrame, int? endFrame)
    {
        MediaId = mediaId;
        Revision = revision;
        Index = index;
        StartFrame = startFrame;
        EndFrame = endFrame;
        Status = VideoSegmentStatus.Pending;
    }

    public Guid MediaId { get; private set; }

    public int Revision { get; private set; }

    /// <summary>0-based position in the video.</summary>
    public int Index { get; private set; }

    /// <summary>First frame of the segment's own part (counted from the start of the video).</summary>
    public int StartFrame { get; private set; }

    /// <summary>Frame after its own part; null for the last segment, which runs to the end of the video.</summary>
    public int? EndFrame { get; private set; }

    public VideoSegmentStatus Status { get; private set; }

    /// <summary>Times a worker has started on it.</summary>
    public int Attempts { get; private set; }

    /// <summary>Frames it wrote.</summary>
    public int? Frames { get; private set; }

    /// <summary>Faces followed through its own part.</summary>
    public int? FacesBlurred { get; private set; }

    /// <summary>Number plates followed through its own part.</summary>
    public int? PlatesBlurred { get; private set; }

    /// <summary>Why a moderator should check it (detector problems, low confidence), one per line; null if none.</summary>
    public string? ReviewReasons { get; private set; }

    /// <summary>How long the worker took, in milliseconds.</summary>
    public long? ElapsedMilliseconds { get; private set; }

    /// <summary>The highest graphic content score of the frames it checked; null if detection was off.</summary>
    public double? SensitiveScore { get; private set; }

    /// <summary>The average CLIP embedding of the frames it scored (512 float32s), combined into the media's summary.</summary>
    public byte[]? ClipSummary { get; private set; }

    /// <summary>How many frames went into <see cref="ClipSummary"/>.</summary>
    public int ClipFrames { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsDone => Status == VideoSegmentStatus.Done;

    /// <summary>Counts a worker starting on it.</summary>
    public void RecordAttempt() => Attempts++;

    /// <summary>
    /// In-memory version of the store's completion: records the results and returns true, or false if it was already
    /// done. The database store does the same in one conditional update.
    /// </summary>
    /// <param name="frames">Frames written.</param>
    /// <param name="faces">Faces followed.</param>
    /// <param name="plates">Plates followed.</param>
    /// <param name="reviewReasons">Why it should be checked; empty if none.</param>
    /// <param name="elapsed">Time taken.</param>
    /// <param name="now">The time.</param>
    /// <param name="sensitiveScore">The highest graphic content score, or null.</param>
    /// <param name="clipSummary">The average CLIP embedding, or null.</param>
    /// <param name="clipFrames">Frames in <paramref name="clipSummary"/>.</param>
    public bool MarkDone(
        int frames,
        int faces,
        int plates,
        IEnumerable<string> reviewReasons,
        TimeSpan elapsed,
        DateTimeOffset now,
        double? sensitiveScore = null,
        byte[]? clipSummary = null,
        int clipFrames = 0)
    {
        if (IsDone)
        {
            return false;
        }

        Status = VideoSegmentStatus.Done;
        Frames = frames;
        FacesBlurred = faces;
        PlatesBlurred = plates;
        ReviewReasons = JoinReasons(reviewReasons);
        ElapsedMilliseconds = (long)elapsed.TotalMilliseconds;
        SensitiveScore = sensitiveScore;
        ClipSummary = clipSummary;
        ClipFrames = clipSummary is null ? 0 : clipFrames;
        CompletedAt = now;
        return true;
    }

    /// <summary>The reasons as a list.</summary>
    public IReadOnlyList<string> ReviewReasonList =>
        string.IsNullOrEmpty(ReviewReasons) ? [] : ReviewReasons.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Joins reasons for storage, within <see cref="ReviewReasonsMaxLength"/>.</summary>
    /// <param name="reasons">The reasons.</param>
    public static string? JoinReasons(IEnumerable<string> reasons)
    {
        var joined = string.Join('\n', reasons.Distinct());
        return joined.Length == 0 ? null : joined.Length <= ReviewReasonsMaxLength ? joined : joined[..ReviewReasonsMaxLength];
    }
}

/// <summary>
/// How a video is split: segments of <see cref="FramesPerSegment"/> output frames, each analysed with
/// <see cref="OverlapFrames"/> extra frames either side. Frames are counted on the output's frame grid from the start
/// of the video, so every segment agrees exactly where the others begin and end, and detection runs on the same frames
/// (every Nth from the start) as it would for the whole video.
/// </summary>
/// <param name="FrameRate">Output frames per second.</param>
/// <param name="FramesPerSegment">Frames in each segment but the last.</param>
/// <param name="OverlapFrames">Extra frames analysed either side.</param>
/// <param name="FirstFrame">Grid position of the video's first frame in the source timeline.</param>
/// <param name="SegmentCount">Number of segments (1 means don't split).</param>
public sealed record VideoSegmentPlan(double FrameRate, int FramesPerSegment, int OverlapFrames, long FirstFrame, int SegmentCount)
{
    /// <summary>
    /// Splits a video of <paramref name="durationSeconds"/> into segments of <paramref name="segmentSeconds"/>. A
    /// video no longer than one segment isn't split (<see cref="SegmentCount"/> 1). A last piece shorter than a
    /// quarter of a segment is added to the one before, rather than being a tiny segment of its own.
    /// </summary>
    /// <param name="durationSeconds">The video's length.</param>
    /// <param name="frameRate">Output frames per second.</param>
    /// <param name="startSeconds">The source's start time (usually 0).</param>
    /// <param name="segmentSeconds">Segment length; 0 means never split.</param>
    /// <param name="overlapSeconds">Overlap analysed either side.</param>
    public static VideoSegmentPlan For(double durationSeconds, double frameRate, double startSeconds, int segmentSeconds, double overlapSeconds)
    {
        var framesPerSegment = Math.Max(1, (int)Math.Round(segmentSeconds * frameRate, MidpointRounding.AwayFromZero));
        var overlap = Math.Max(0, (int)Math.Ceiling(overlapSeconds * frameRate));
        var first = (long)Math.Round(Math.Max(0, startSeconds) * frameRate, MidpointRounding.AwayFromZero);
        if (segmentSeconds <= 0 || durationSeconds <= segmentSeconds)
        {
            return new VideoSegmentPlan(frameRate, framesPerSegment, overlap, first, 1);
        }

        var totalFrames = Math.Max(1, (int)Math.Floor(durationSeconds * frameRate));
        var count = (int)Math.Ceiling(totalFrames / (double)framesPerSegment);
        var lastPiece = totalFrames - ((count - 1) * framesPerSegment);
        if (count > 1 && lastPiece < framesPerSegment / 4.0)
        {
            count--;
        }

        return new VideoSegmentPlan(frameRate, framesPerSegment, overlap, first, Math.Max(1, count));
    }

    public bool IsSplit => SegmentCount > 1;

    /// <summary>First frame of segment <paramref name="index"/>'s own part.</summary>
    /// <param name="index">The segment.</param>
    public int CoreStart(int index) => index * FramesPerSegment;

    /// <summary>Frame after segment <paramref name="index"/>'s own part; null for the last (to the end).</summary>
    /// <param name="index">The segment.</param>
    public int? CoreEnd(int index) => index >= SegmentCount - 1 ? null : (index + 1) * FramesPerSegment;
}
