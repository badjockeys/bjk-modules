namespace BJK.Modules.Media.Domain;

/// <summary>How a known image is identified.</summary>
public enum KnownHashKind
{
    /// <summary>Meta's PDQ perceptual hash (64 hex digits): also matches resized, recompressed or slightly edited copies.</summary>
    Pdq,

    /// <summary>SHA-256 of the file (64 hex digits): exact copies only.</summary>
    Sha256,

    /// <summary>MD5 of the file (32 hex digits): exact copies only. Some hash lists still share these.</summary>
    Md5,
}

/// <summary>
/// One hash of an image that must never be published (<c>media.KnownImageHashes</c>): from a hash list such as the IWF's
/// (written by a sync job, <see cref="Source"/> naming the list), added by a moderator who removed the image, or a
/// harmless test hash on development and test machines. The media worker keeps the active hashes in memory and checks
/// every upload against them before anything is published.
/// </summary>
public sealed class KnownImageHash
{
    public const int SourceMaxLength = 50;
    public const int ReferenceMaxLength = 200;

    /// <summary>The source for hashes moderators add when they remove an image.</summary>
    public const string ModerationSource = "Moderation";

    /// <summary>The source for harmless test hashes (local and test environments).</summary>
    public const string TestSource = "Test";

    // For EF Core.
    private KnownImageHash()
    {
        Value = string.Empty;
        Source = string.Empty;
    }

    /// <param name="kind">The kind of hash.</param>
    /// <param name="value">The hash, hex (any case; stored lower case).</param>
    /// <param name="source">Where it came from: a list's name, <see cref="ModerationSource"/> or <see cref="TestSource"/>.</param>
    /// <param name="reference">The list's own id for it, or the media it came from; optional.</param>
    /// <param name="addedAt">When it was added.</param>
    /// <param name="addedBy">The moderator who added it, if one did.</param>
    public KnownImageHash(KnownHashKind kind, string value, string source, string? reference, DateTimeOffset addedAt, Guid? addedBy = null)
    {
        var normalised = Normalise(kind, value)
            ?? throw new ArgumentException($"Not a valid {kind} hash.", nameof(value));
        Id = Guid.NewGuid();
        Kind = kind;
        Value = normalised;
        Source = source;
        Reference = reference;
        AddedAt = addedAt;
        AddedBy = addedBy;
    }

    public Guid Id { get; private set; }

    public KnownHashKind Kind { get; private set; }

    /// <summary>Lower-case hex.</summary>
    public string Value { get; private set; }

    public string Source { get; private set; }

    public string? Reference { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    public Guid? AddedBy { get; private set; }

    /// <summary>The hash in lower case if it's valid for its kind, otherwise null.</summary>
    /// <param name="kind">The kind of hash.</param>
    /// <param name="value">The hash as given.</param>
    public static string? Normalise(KnownHashKind kind, string? value)
    {
        var hex = value?.Trim().ToLowerInvariant();
        var length = kind == KnownHashKind.Md5 ? 32 : 64;
        return hex is not null && hex.Length == length && hex.All(Uri.IsHexDigit) ? hex : null;
    }
}

/// <summary>
/// An upload that matched a known image (<c>media.KnownImageMatches</c>). The upload is blocked and its files deleted;
/// this record is what's kept, for moderators and for reporting to the authorities. It holds no picture.
/// </summary>
public sealed class KnownImageMatch
{
    // For EF Core.
    private KnownImageMatch()
    {
        Source = string.Empty;
        MatchedValue = string.Empty;
    }

    /// <param name="mediaId">The blocked media.</param>
    /// <param name="userId">Who uploaded it.</param>
    /// <param name="hit">What it matched.</param>
    /// <param name="sha256">SHA-256 of the uploaded file.</param>
    /// <param name="matchedAt">When.</param>
    public KnownImageMatch(Guid mediaId, Guid userId, KnownImageHit hit, string? sha256, DateTimeOffset matchedAt)
    {
        Id = Guid.NewGuid();
        MediaId = mediaId;
        UserId = userId;
        HashId = hit.HashId;
        Kind = hit.Kind;
        MatchedValue = hit.Value;
        Source = hit.Source;
        Reference = hit.Reference;
        Distance = hit.Distance;
        Sha256 = sha256;
        MatchedAt = matchedAt;
    }

    public Guid Id { get; private set; }

    public Guid MediaId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>The matching row of <c>media.KnownImageHashes</c> (it may since have been removed from the list).</summary>
    public Guid HashId { get; private set; }

    public KnownHashKind Kind { get; private set; }

    /// <summary>The list's hash that matched.</summary>
    public string MatchedValue { get; private set; }

    public string Source { get; private set; }

    public string? Reference { get; private set; }

    /// <summary>For PDQ: how many bits differed (0 is identical). Null for file hashes.</summary>
    public int? Distance { get; private set; }

    public string? Sha256 { get; private set; }

    public DateTimeOffset MatchedAt { get; private set; }

    /// <summary>When a moderator marked it reported to the authorities, if they have.</summary>
    public DateTimeOffset? ReportedAt { get; private set; }

    public string? ReportReference { get; private set; }

    /// <summary>Records that the match was reported (e.g. to the NCA or the IWF), with their reference.</summary>
    /// <param name="reference">Their reference.</param>
    /// <param name="at">When.</param>
    public void MarkReported(string reference, DateTimeOffset at)
    {
        ReportReference = reference;
        ReportedAt = at;
    }
}

/// <summary>A known-image list entry an upload matched.</summary>
/// <param name="HashId">The list row.</param>
/// <param name="Kind">The kind of hash.</param>
/// <param name="Value">The list's hash.</param>
/// <param name="Source">Its source.</param>
/// <param name="Reference">Its reference, if any.</param>
/// <param name="Distance">For PDQ, the bits that differed.</param>
public sealed record KnownImageHit(Guid HashId, KnownHashKind Kind, string Value, string Source, string? Reference, int? Distance)
{
    /// <summary>
    /// Review reasons starting with this mean a frame matched a known image. The redactor reports matches this way so
    /// they survive the segment path (segment results are stored as reasons); the processor blocks the media, and the
    /// reason is never shown to anyone.
    /// </summary>
    public const string ReasonPrefix = "known-image:";

    /// <summary>As a review reason: the prefix, the list row and the distance.</summary>
    public string ToReason() => $"{ReasonPrefix}{HashId:N}:{Distance ?? 0}";

    /// <summary>The list row and distance from a reason made by <see cref="ToReason"/>, or null if it isn't one.</summary>
    /// <param name="reason">A review reason.</param>
    public static (Guid HashId, int Distance)? FromReason(string reason)
    {
        if (!reason.StartsWith(ReasonPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = reason[ReasonPrefix.Length..].Split(':');
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var id) && int.TryParse(parts[1], out var distance)
            ? (id, distance)
            : null;
    }
}
