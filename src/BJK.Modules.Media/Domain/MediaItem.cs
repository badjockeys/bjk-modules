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

    private static readonly JsonSerializerOptions RedactionJson = new(JsonSerializerDefaults.Web);

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

    /// <summary>When the storage lifecycle rule removes the original (upload time + retention).</summary>
    public DateTimeOffset? OriginalDeletesAt { get; private set; }

    public string? PlaybackKey { get; private set; }

    public string? ThumbnailKey { get; private set; }

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
        ReviewReason = null;
        Status = MediaStatus.Ready;
    }

    /// <summary>Records how many faces and plates the worker blurred. Called before <see cref="MarkReady"/> or <see cref="MarkNeedsReview"/>.</summary>
    /// <param name="faces">Faces blurred.</param>
    /// <param name="plates">Number plates blurred.</param>
    public void RecordRedaction(int faces, int plates)
    {
        FacesBlurred = faces;
        PlatesBlurred = plates;
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
        ReviewReason = reason.Length <= ReviewReasonMaxLength ? reason : reason[..ReviewReasonMaxLength];
        Status = MediaStatus.NeedsReview;
    }

    /// <summary>A moderator approved the blurred files, which have been copied to the public bucket. It can now be posted.</summary>
    /// <param name="moderatorId">The moderator, or null for a machine token.</param>
    /// <param name="now">The time.</param>
    public void Approve(Guid? moderatorId, DateTimeOffset now)
    {
        EnsureStatus(MediaStatus.NeedsReview);
        PlaybackKey = MediaKeys.Playback(UserId, Id, MediaType, Revision);
        ThumbnailKey = MediaKeys.Thumbnail(UserId, Id, MediaType, Revision);
        ReviewReason = null;
        ReviewedBy = moderatorId;
        ReviewedAt = now;
        Status = MediaStatus.Ready;
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
    public void AddManualRedactions(IReadOnlyCollection<ManualRedaction> regions, DateTimeOffset now)
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
        PlaybackKey = null;
        ThumbnailKey = null;
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

        FailureReason = reason.Length <= FailureReasonMaxLength ? reason : reason[..FailureReasonMaxLength];
        Status = MediaStatus.Failed;
        UpdatedAt = now;
    }

    public void Delete(DateTimeOffset now)
    {
        if (Status is MediaStatus.Deleted)
        {
            return;
        }

        Status = MediaStatus.Deleted;
        DeletedAt = now;
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
