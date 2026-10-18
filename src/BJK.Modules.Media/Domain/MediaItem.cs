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
        PlaybackKey = MediaKeys.Playback(UserId, Id, MediaType);
        ThumbnailKey = MediaKeys.Thumbnail(UserId, Id, MediaType);
        PlaybackSizeBytes = playbackSizeBytes;
        DurationSeconds = durationSeconds;
        Width = width;
        Height = height;
        Sha256 = sha256;
        FailureReason = null;
        ProcessedAt = now;
        Status = MediaStatus.Ready;
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
