using BJK.Modules.Shared.Security;

namespace BJK.Modules.Media.Domain;

/// <summary>How a clip got into the vault.</summary>
public enum VaultClipSource
{
    /// <summary>The original of a photo or video the person posted, kept when the post was published.</summary>
    Post,

    /// <summary>Uploaded straight to the vault, without posting.</summary>
    Upload,
}

public enum VaultClipStatus
{
    /// <summary>An upload link has been issued; the file may not have arrived yet.</summary>
    Uploading,

    /// <summary>In the vault.</summary>
    Stored,
}

/// <summary>
/// One original in someone's vault (owner, 23 Oct 2026): the unblurred file, with its sound and GPS, kept privately for
/// them as evidence, until <see cref="ExpiresAt"/> (30 days on Free, 12 months on Premium, 3 years on Premium +). Only
/// its owner can see or download it. Stored in <c>media.VaultClips</c>; the file is in the vault bucket.
/// </summary>
public sealed class VaultClip
{
    public const int FileNameMaxLength = 255;

    // For EF Core.
    private VaultClip()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public VaultClipSource Source { get; private set; }

    public VaultClipStatus Status { get; private set; }

    public MediaType MediaType { get; private set; }

    /// <summary>The name it had on the person's device, for them to recognise it. Never used in keys or downloads.</summary>
    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    /// <summary>Declared while uploading; the real size once stored. Counts towards the person's space.</summary>
    public long SizeBytes { get; private set; }

    /// <summary>The file in the vault bucket (<see cref="MediaKeys.VaultClip"/>).</summary>
    public string Key { get; private set; } = string.Empty;

    /// <summary>For <see cref="VaultClipSource.Post"/>: the media it's the original of.</summary>
    public Guid? MediaId { get; private set; }

    /// <summary>For <see cref="VaultClipSource.Post"/>: the post, if the Posts module said (for a link back).</summary>
    public Guid? PostId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StoredAt { get; private set; }

    /// <summary>When it's deleted automatically. Follows the person's plan (<see cref="FollowPlan"/>).</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Starts an upload straight to the vault.</summary>
    /// <param name="userId">Whose vault.</param>
    /// <param name="fileName">The name on their device (trimmed to <see cref="FileNameMaxLength"/>).</param>
    /// <param name="contentType">A photo or video type <see cref="MediaFormats"/> knows.</param>
    /// <param name="sizeBytes">The declared size.</param>
    /// <param name="now">The time.</param>
    /// <param name="retention">How long their plan keeps clips.</param>
    public static VaultClip StartUpload(Guid userId, string fileName, string contentType, long sizeBytes, DateTimeOffset now, TimeSpan retention)
    {
        if (!MediaFormats.TryGet(contentType, out var type, out var extension))
        {
            throw new ArgumentException($"Unsupported content type '{contentType}'.", nameof(contentType));
        }

        var id = Guid.CreateVersion7(now);
        return new VaultClip
        {
            Id = id,
            UserId = userId,
            Source = VaultClipSource.Upload,
            Status = VaultClipStatus.Uploading,
            MediaType = type,
            FileName = CleanName(fileName),
            ContentType = contentType.Trim().ToLowerInvariant(),
            SizeBytes = sizeBytes,
            Key = MediaKeys.VaultClip(userId, id, extension),
            CreatedAt = now,
            ExpiresAt = now + retention,
        };
    }

    /// <summary>The original of a published post's media, copied into the vault.</summary>
    /// <param name="media">The media, with its original still in storage.</param>
    /// <param name="postId">The post, if known.</param>
    /// <param name="now">The time.</param>
    /// <param name="retention">How long the person's plan keeps clips.</param>
    public static VaultClip FromPost(MediaItem media, Guid? postId, DateTimeOffset now, TimeSpan retention)
    {
        var extension = MediaFormats.TryGet(media.OriginalContentType, out _, out var ext) ? ext : "bin";
        var id = Guid.CreateVersion7(now);
        return new VaultClip
        {
            Id = id,
            UserId = media.UserId,
            Source = VaultClipSource.Post,
            Status = VaultClipStatus.Stored,
            MediaType = media.MediaType,
            FileName = CleanName(media.OriginalFileName),
            ContentType = media.OriginalContentType,
            SizeBytes = media.OriginalSizeBytes ?? 0,
            Key = MediaKeys.VaultClip(media.UserId, id, extension),
            MediaId = media.Id,
            PostId = postId,
            CreatedAt = now,
            StoredAt = now,
            ExpiresAt = now + retention,
        };
    }

    public bool IsOwnedBy(Guid userId) => UserId == userId;

    /// <summary>The file has arrived.</summary>
    /// <param name="sizeBytes">Its real size.</param>
    /// <param name="now">The time.</param>
    public void MarkStored(long sizeBytes, DateTimeOffset now)
    {
        if (Status != VaultClipStatus.Uploading)
        {
            throw new InvalidOperationException($"Vault clip {Id} is already stored.");
        }

        Status = VaultClipStatus.Stored;
        SizeBytes = sizeBytes;
        StoredAt = now;
    }

    /// <summary>
    /// Brings the expiry into line with the person's plan: kept longer straight away after an upgrade; after a downgrade,
    /// clips the new plan would already have deleted get <paramref name="notice"/> before they go.
    /// </summary>
    /// <param name="retention">How long the plan keeps clips.</param>
    /// <param name="now">The time.</param>
    /// <param name="notice">How long clips are kept after a downgrade that puts them out of date.</param>
    /// <returns>True if the expiry changed.</returns>
    public bool FollowPlan(TimeSpan retention, DateTimeOffset now, TimeSpan notice)
    {
        var target = CreatedAt + retention;
        if (target > ExpiresAt)
        {
            ExpiresAt = target;
            return true;
        }

        if (target < ExpiresAt)
        {
            var earliest = now + notice;
            var shortened = target > earliest ? target : earliest;
            if (shortened < ExpiresAt)
            {
                ExpiresAt = shortened;
                return true;
            }
        }

        return false;
    }

    /// <summary>The person's own name for it: no folders, no control characters, at most 255 characters.</summary>
    internal static string CleanName(string? name)
    {
        var file = Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
        var clean = new string(file.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0)
        {
            clean = "clip";
        }

        return clean.Length <= FileNameMaxLength ? clean : clean[..FileNameMaxLength];
    }
}

/// <summary>
/// The plan we last saw someone on (<c>media.VaultAccounts</c>), so the vault knows how long to keep their clips when
/// nobody's signed in, e.g. when a post is published by a background job or the hourly cleanup runs. Updated whenever
/// they use the vault or upload anything.
/// </summary>
public sealed class VaultAccount
{
    // For EF Core.
    private VaultAccount()
    {
    }

    public Guid UserId { get; private set; }

    public SubscriptionPlan Plan { get; private set; }

    public DateTimeOffset PlanSeenAt { get; private set; }

    /// <summary>
    /// True when the person has turned off keeping their posts' originals automatically. Stored this way round so
    /// everyone, including people who've never opened their vault, keeps them by default.
    /// </summary>
    public bool AutoSaveOff { get; private set; }

    /// <summary>Whether the originals of this person's published posts go into their vault (on by default).</summary>
    public bool AutoSave => !AutoSaveOff;

    public static VaultAccount Create(Guid userId, SubscriptionPlan plan, DateTimeOffset now) =>
        new() { UserId = userId, Plan = plan, PlanSeenAt = now };

    /// <summary>Turns keeping their posts' originals automatically on or off. Clips already in the vault stay.</summary>
    public void SetAutoSave(bool on) => AutoSaveOff = !on;

    /// <summary>Records the plan seen now.</summary>
    /// <returns>True if it's a different plan from before.</returns>
    public bool See(SubscriptionPlan plan, DateTimeOffset now)
    {
        PlanSeenAt = now;
        if (plan == Plan)
        {
            return false;
        }

        Plan = plan;
        return true;
    }
}
