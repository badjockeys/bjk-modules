using System.Text.Json;

namespace BJK.Modules.Media.Domain;

/// <summary>
/// One uploaded photo or video and everything we know about its files. Stored in <c>media.Media</c>.
/// (Named MediaItem because a class called Media would clash with the BJK.Modules.Media namespace.)
/// </summary>
public sealed class MediaItem
{
    public const int OriginalFileNameMaxLength = 255;
    public const int ContentTypeMaxLength = 100;
    public const int KeyMaxLength = 512;
    public const int FailureReasonMaxLength = 500;
    public const int ReviewReasonMaxLength = 500;
    public const int PerceptualHashesMaxLength = 65 * MaxPerceptualHashes;

    /// <summary>How many PDQ hashes are kept per media (sampled through a video).</summary>
    public const int MaxPerceptualHashes = 8;

    private static readonly JsonSerializerOptions RedactionJson = new(JsonSerializerDefaults.Web);

    // Status changes other modules hear about (media.ready / media.failed); turned into outbox rows when saved.
    private readonly List<MediaStatusChange> _statusChanges = [];

    // For EF Core.
    private MediaItem()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The uploader's account id (the "sub" claim). The first part of every storage key.</summary>
    public Guid UserId { get; private set; }

    public MediaType MediaType { get; private set; }

    public MediaStatus Status { get; private set; }

    /// <summary>The file name as uploaded. For display and audit only; never used in storage keys.</summary>
    public string OriginalFileName { get; private set; } = string.Empty;

    public string OriginalContentType { get; private set; } = string.Empty;

    public long? OriginalSizeBytes { get; private set; }

    public string OriginalKey { get; private set; } = string.Empty;

    /// <summary>
    /// The latest the original is kept once processing has finished (upload time + <c>Media:UnusedOriginalRetention</c>,
    /// 24 hours): the API host's sweeper deletes it then if nothing released it sooner (failed media, or Ready media that
    /// was never posted). Media still processing or waiting for review keeps it.
    /// </summary>
    public DateTimeOffset? OriginalDeletesAt { get; private set; }

    /// <summary>
    /// When the uploaded original was deleted: as soon as its post was published (or by the sweeper). Null while it
    /// exists. Nothing is ever processed from it again: moderators' extra blurs work from the processed file.
    /// </summary>
    public DateTimeOffset? OriginalDeletedAt { get; private set; }

    /// <summary>
    /// While re-processing after a moderator added blur regions: the copy of the processed file to work from (in the
    /// originals bucket, <see cref="MediaKeys.ReworkSource"/>), instead of the original. Null for a first processing.
    /// </summary>
    public string? SourceKey { get; private set; }

    public string? PlaybackKey { get; private set; }

    public string? ThumbnailKey { get; private set; }

    /// <summary>
    /// Photos only: the feed-size copy (<see cref="MediaKeys.Feed"/>), set once Ready. Null for videos and for photos
    /// processed before feed copies existed (the apps then use the full image).
    /// </summary>
    public string? FeedKey { get; private set; }

    public long? PlaybackSizeBytes { get; private set; }

    public decimal? DurationSeconds { get; private set; }

    public int? Width { get; private set; }

    public int? Height { get; private set; }

    /// <summary>SHA-256 of the original, hex. Set by the worker; used to spot duplicates.</summary>
    public string? Sha256 { get; private set; }

    public int ProcessingAttempts { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>Why the media needs a moderator's review (set while <see cref="MediaStatus.NeedsReview"/>).</summary>
    public string? ReviewReason { get; private set; }

    /// <summary>Faces found and blurred automatically in the latest processing (in a video, separate faces followed through it).</summary>
    public int? FacesBlurred { get; private set; }

    /// <summary>Number plates found and blurred automatically in the latest processing.</summary>
    public int? PlatesBlurred { get; private set; }

    /// <summary>
    /// How many times a moderator has had the media re-processed. Part of the processed file names, so a new version
    /// never reuses a cached address.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>The moderator who last approved or rejected it (null for a machine token).</summary>
    public Guid? ReviewedBy { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    /// <summary>Blur regions added by moderators, as JSON. Use <see cref="ManualRedactions"/>.</summary>
    public string? ManualRedactionsJson { get; private set; }

    /// <summary>Blur regions added by moderators, applied on every processing on top of automatic detection.</summary>
    public IReadOnlyList<ManualRedaction> ManualRedactions =>
        string.IsNullOrEmpty(ManualRedactionsJson)
            ? []
            : JsonSerializer.Deserialize<List<ManualRedaction>>(ManualRedactionsJson, RedactionJson) ?? [];

    /// <summary>
    /// True while the processed files (<see cref="PlaybackKey"/>, <see cref="ThumbnailKey"/>) are kept only in the
    /// sensitive bucket, under <see cref="MediaKeys.Sensitive"/>, because the media is in a post tagged sensitive. The
    /// public bucket then holds only the blurred <see cref="PreviewKey"/>. The keys keep their public names either way.
    /// </summary>
    public bool IsRestricted { get; private set; }

    /// <summary>The heavily blurred preview in the public bucket, while the media is sensitive (null until it's made).</summary>
    public string? PreviewKey { get; private set; }

    /// <summary>
    /// The highest graphic content score (0 to 1) automatic detection gave it in the latest processing, whether or not
    /// it crossed the threshold; null if detection was off. Kept to tune the threshold against moderators' decisions.
    /// </summary>
    public double? SensitiveScore { get; private set; }

    /// <summary>
    /// The highest nudity score (0 to 1) of the frames checked in the latest processing; null if not checked. At or
    /// above <c>Media:SensitiveDetection:NudityThreshold</c> the media was held for a moderator.
    /// </summary>
    public double? NudityScore { get; private set; }

    /// <summary>
    /// PDQ hashes of the original (one for a photo, up to <see cref="MaxPerceptualHashes"/> sampled through a video),
    /// space-separated, so a moderator who removes it can block copies of it. Null for videos processed in segments.
    /// </summary>
    public string? PerceptualHashes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UploadedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Stops the API and a worker overwriting each other's changes.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public static MediaItem Create(
        Guid userId,
        string originalFileName,
        string contentType,
        DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user id is required.", nameof(userId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        if (!MediaFormats.TryGet(contentType, out var type, out var extension))
        {
            throw new ArgumentException($"'{contentType}' is not a supported file type.", nameof(contentType));
        }

        var id = Guid.CreateVersion7(now);
        return new MediaItem
        {
            Id = id,
            UserId = userId,
            MediaType = type,
            Status = MediaStatus.Uploading,
            OriginalFileName = TrimFileName(originalFileName),
            OriginalContentType = contentType.Trim().ToLowerInvariant(),
            OriginalKey = MediaKeys.Original(userId, id, extension),
            CreatedAt = now,
        };
    }

    public bool IsOwnedBy(Guid userId) => UserId == userId;

    /// <summary>The file has arrived in storage; processing can start.</summary>
    /// <param name="sizeBytes">The original's size.</param>
    /// <param name="now">The time.</param>
    /// <param name="originalRetention">How long the original may be kept at most once processing is finished.</param>
    public void MarkUploaded(long sizeBytes, DateTimeOffset now, TimeSpan originalRetention)
    {
        EnsureStatus(MediaStatus.Uploading);
        OriginalSizeBytes = sizeBytes;
        UploadedAt = now;
        OriginalDeletesAt = now + originalRetention;
        Status = MediaStatus.Processing;
        UpdatedAt = now;
    }

    /// <summary>Called by the worker each time it starts on this media.</summary>
    public void RecordProcessingAttempt(DateTimeOffset now)
    {
        ProcessingAttempts++;
        UpdatedAt = now;
    }

    /// <summary>Processed files are published. Called by the worker.</summary>
    public void MarkReady(
        long playbackSizeBytes,
        decimal? durationSeconds,
        int? width,
        int? height,
        string? sha256,
        DateTimeOffset now)
    {
        EnsureStatus(MediaStatus.Processing);
        RecordProcessed(playbackSizeBytes, durationSeconds, width, height, sha256, now);
        PlaybackKey = MediaKeys.Playback(UserId, Id, MediaType, Revision);
        ThumbnailKey = MediaKeys.Thumbnail(UserId, Id, MediaType, Revision);
        FeedKey = MediaType == MediaType.Image ? MediaKeys.Feed(UserId, Id, Revision) : null;
        ReviewReason = null;
        Status = MediaStatus.Ready;
        _statusChanges.Add(new MediaStatusChange(MediaStatus.Ready, null, now));
    }

    /// <summary>Records how many faces and plates the worker blurred. Called before <see cref="MarkReady"/> or <see cref="MarkNeedsReview"/>.</summary>
    /// <param name="faces">Faces blurred.</param>
    /// <param name="plates">Number plates blurred.</param>
    public void RecordRedaction(int faces, int plates)
    {
        FacesBlurred = faces;
        PlatesBlurred = plates;
    }

    /// <summary>Records the graphic content score of the latest processing (null if detection was off).</summary>
    /// <param name="score">The highest score of the frames checked.</param>
    public void RecordSensitiveScore(double? score) =>
        SensitiveScore = score is null ? null : Math.Round(Math.Clamp(score.Value, 0, 1), 4);

    /// <summary>Records the nudity score and the original's perceptual hashes from the latest processing.</summary>
    /// <param name="nudityScore">The highest nudity score of the frames checked, or null.</param>
    /// <param name="perceptualHashes">PDQ hashes (hex); the first <see cref="MaxPerceptualHashes"/> are kept.</param>
    public void RecordContentChecks(double? nudityScore, IEnumerable<string>? perceptualHashes)
    {
        NudityScore = nudityScore is null ? null : Math.Round(Math.Clamp(nudityScore.Value, 0, 1), 4);
        var hashes = perceptualHashes?.Take(MaxPerceptualHashes).ToList();
        PerceptualHashes = hashes is { Count: > 0 } ? string.Join(' ', hashes) : null;
    }

    /// <summary>
    /// Processed and blurred, but a moderator must check it first. The blurred files are in the private bucket under
    /// <see cref="MediaKeys.ReviewPlayback"/>; nothing is public, so the public keys stay empty.
    /// </summary>
    /// <param name="reason">Why it needs review, for moderators.</param>
    /// <param name="playbackSizeBytes">Size of the blurred main file.</param>
    /// <param name="durationSeconds">Video length; null for photos.</param>
    /// <param name="width">Width as seen.</param>
    /// <param name="height">Height as seen.</param>
    /// <param name="sha256">SHA-256 of the original.</param>
    /// <param name="now">The time.</param>
    public void MarkNeedsReview(
        string reason,
        long playbackSizeBytes,
        decimal? durationSeconds,
        int? width,
        int? height,
        string? sha256,
        DateTimeOffset now)
    {
        EnsureStatus(MediaStatus.Processing);
        RecordProcessed(playbackSizeBytes, durationSeconds, width, height, sha256, now);
        PlaybackKey = null;
        ThumbnailKey = null;
        FeedKey = null;
        ReviewReason = reason.Length <= ReviewReasonMaxLength ? reason : reason[..ReviewReasonMaxLength];
        Status = MediaStatus.NeedsReview;
    }

    /// <summary>A moderator approved the blurred files, which have been copied to the public bucket. It can now be posted.</summary>
    /// <param name="moderatorId">The moderator, or null for a machine token.</param>
    /// <param name="now">The time.</param>
    /// <param name="withFeed">Photos: a feed-size copy was published too (false for photos processed before they existed).</param>
    public void Approve(Guid? moderatorId, DateTimeOffset now, bool withFeed = true)
    {
        EnsureStatus(MediaStatus.NeedsReview);
        PlaybackKey = MediaKeys.Playback(UserId, Id, MediaType, Revision);
        ThumbnailKey = MediaKeys.Thumbnail(UserId, Id, MediaType, Revision);
        FeedKey = MediaType == MediaType.Image && withFeed ? MediaKeys.Feed(UserId, Id, Revision) : null;
        ReviewReason = null;
        ReviewedBy = moderatorId;
        ReviewedAt = now;
        Status = MediaStatus.Ready;
        UpdatedAt = now;
        _statusChanges.Add(new MediaStatusChange(MediaStatus.Ready, null, now));
    }

    /// <summary>
    /// The processed files are now kept only in the sensitive bucket (the media is in a post tagged sensitive), with the
    /// blurred preview, if it has been made yet, in the public bucket. Only for Ready media.
    /// </summary>
    /// <param name="previewKey">The blurred preview's key in the public bucket, or null if it isn't made yet.</param>
    /// <param name="now">The time.</param>
    public void Restrict(string? previewKey, DateTimeOffset now)
    {
        EnsureStatus(MediaStatus.Ready);
        IsRestricted = true;
        PreviewKey = previewKey;
        UpdatedAt = now;
    }

    /// <summary>The processed files are public again (the sensitive tag was removed) and the blurred preview is no longer used.</summary>
    /// <param name="now">The time.</param>
    public void Unrestrict(DateTimeOffset now)
    {
        EnsureStatus(MediaStatus.Ready);
        IsRestricted = false;
        PreviewKey = null;
        UpdatedAt = now;
    }

    /// <summary>The original has been deleted (its post was published, or the sweeper's deadline passed).</summary>
    /// <param name="now">The time.</param>
    public void MarkOriginalDeleted(DateTimeOffset now)
    {
        OriginalDeletedAt ??= now;
        UpdatedAt = now;
    }

    /// <summary>A moderator rejected it: it becomes <see cref="MediaStatus.Failed"/> with the reason, which the uploader sees.</summary>
    /// <param name="reason">Shown to the uploader.</param>
    /// <param name="moderatorId">The moderator, or null for a machine token.</param>
    /// <param name="now">The time.</param>
    public void Reject(string reason, Guid? moderatorId, DateTimeOffset now)
    {
        EnsureStatus(MediaStatus.NeedsReview);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ReviewReason = null;
        ReviewedBy = moderatorId;
        ReviewedAt = now;
        MarkFailed(reason, now);
    }

    /// <summary>
    /// A moderator added blur regions: they're stored and the media goes back to <see cref="MediaStatus.Processing"/>
    /// under a new <see cref="Revision"/>. The caller removes the previous files and queues a processing job. Allowed
    /// while waiting for review or once Ready (for example after a report).
    /// </summary>
    /// <param name="regions">Regions to add to any already there.</param>
    /// <param name="now">The time.</param>
    /// <param name="sourceKey">
    /// The copy of the current processed file to re-process from (<see cref="MediaKeys.ReworkSource"/>); originals
    /// aren't kept once a post is published.
    /// </param>
    public void AddManualRedactions(IReadOnlyCollection<ManualRedaction> regions, DateTimeOffset now, string? sourceKey = null)
    {
        if (Status is not (MediaStatus.NeedsReview or MediaStatus.Ready))
        {
            throw new InvalidOperationException($"Media is {Status}; blur regions can only be added once it has been processed.");
        }

        var all = ManualRedactions.Concat(regions).ToList();
        if (all.Count > ManualRedaction.MaxPerMedia)
        {
            throw new InvalidOperationException($"Media can have at most {ManualRedaction.MaxPerMedia} blur regions.");
        }

        ManualRedactionsJson = JsonSerializer.Serialize(all, RedactionJson);
        Revision++;
        SourceKey = sourceKey ?? SourceKey;
        PlaybackKey = null;
        ThumbnailKey = null;
        FeedKey = null;
        IsRestricted = false;
        PreviewKey = null;
        ReviewReason = null;
        Status = MediaStatus.Processing;
        UpdatedAt = now;
    }

    private void RecordProcessed(long playbackSizeBytes, decimal? durationSeconds, int? width, int? height, string? sha256, DateTimeOffset now)
    {
        PlaybackSizeBytes = playbackSizeBytes;
        DurationSeconds = durationSeconds;
        Width = width;
        Height = height;
        Sha256 = sha256;
        FailureReason = null;
        ProcessedAt = now;
        UpdatedAt = now;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        if (Status is MediaStatus.Deleted)
        {
            return;
        }

        var wasFailed = Status == MediaStatus.Failed;
        FailureReason = reason.Length <= FailureReasonMaxLength ? reason : reason[..FailureReasonMaxLength];
        Status = MediaStatus.Failed;
        UpdatedAt = now;
        if (!wasFailed)
        {
            _statusChanges.Add(new MediaStatusChange(MediaStatus.Failed, FailureReason, now));
        }
    }

    public void Delete(DateTimeOffset now)
    {
        if (Status is MediaStatus.Deleted)
        {
            return;
        }

        // Deleted before it was ready: anything waiting for it (a post) will never get it.
        var wasPending = Status is MediaStatus.Uploading or MediaStatus.Processing or MediaStatus.NeedsReview;
        Status = MediaStatus.Deleted;
        DeletedAt = now;
        UpdatedAt = now;
        if (wasPending)
        {
            _statusChanges.Add(new MediaStatusChange(MediaStatus.Deleted, DeletedReason, now));
        }
    }

    /// <summary>The reason given to waiting posts when media is deleted before it was ready.</summary>
    public const string DeletedReason = "The photo or video was deleted before it finished processing.";

    /// <summary>
    /// Status changes since the last call that other modules should hear about (Ready, Failed, or deleted before it
    /// was ready), oldest first. The database context turns them into outbox messages when it saves.
    /// </summary>
    public IReadOnlyList<MediaStatusChange> TakeStatusChanges()
    {
        var changes = _statusChanges.ToList();
        _statusChanges.Clear();
        return changes;
    }

    /// <summary>
    /// Records what ffprobe and the download found, before the video is split into segments, so the final step has
    /// them. The status doesn't change.
    /// </summary>
    /// <param name="durationSeconds">Video length.</param>
    /// <param name="width">Width as seen.</param>
    /// <param name="height">Height as seen.</param>
    /// <param name="sha256">SHA-256 of the original.</param>
    /// <param name="now">The time.</param>
    public void RecordProbe(decimal? durationSeconds, int? width, int? height, string? sha256, DateTimeOffset now)
    {
        EnsureStatus(MediaStatus.Processing);
        DurationSeconds = durationSeconds;
        Width = width;
        Height = height;
        Sha256 = sha256;
        UpdatedAt = now;
    }

    private void EnsureStatus(MediaStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Media is {Status}, not {expected}.");
        }
    }

    private static string TrimFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Trim());
        return name.Length <= OriginalFileNameMaxLength ? name : name[..OriginalFileNameMaxLength];
    }
}

/// <summary>A status change other modules should hear about.</summary>
/// <param name="Status">Ready, Failed or Deleted.</param>
/// <param name="Reason">Why it failed or was removed, for the uploader.</param>
/// <param name="At">When.</param>
public sealed record MediaStatusChange(MediaStatus Status, string? Reason, DateTimeOffset At);
