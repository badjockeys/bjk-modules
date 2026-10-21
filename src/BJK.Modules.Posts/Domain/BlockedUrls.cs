using BJK.Modules.Shared.Safety;

namespace BJK.Modules.Posts.Domain;

/// <summary>
/// A web address that must not be shared (<c>posts.BlockedUrls</c>): from a list such as the IWF URL List (written by
/// a sync job, <see cref="Source"/> naming the list), added by a moderator, or a harmless test entry locally. Posts and
/// comments containing it are held for review. An entry that's only a host (e.g. "example.com") also covers its
/// subdomains and every page on it; one with a path covers that page and the pages under it.
/// </summary>
public sealed class BlockedUrl
{
    public const int PatternMaxLength = 400;
    public const int SourceMaxLength = 50;
    public const int ReferenceMaxLength = 200;
    public const string ModerationSource = "Moderation";
    public const string TestSource = "Test";

    // For EF Core.
    private BlockedUrl()
    {
        Pattern = string.Empty;
        Source = string.Empty;
    }

    /// <param name="address">The address, in any common form ("https://www.example.com/x", "example.com/x").</param>
    /// <param name="source">Where it came from.</param>
    /// <param name="reference">The list's own id for it, or a moderator's note.</param>
    /// <param name="addedAt">When.</param>
    /// <param name="addedBy">The moderator, if one added it.</param>
    public BlockedUrl(string address, string source, string? reference, DateTimeOffset addedAt, Guid? addedBy = null)
    {
        Pattern = BlockedUrls.Normalise(address) is { Length: <= PatternMaxLength } normalised
            ? normalised
            : throw new ArgumentException("Not a web address.", nameof(address));
        Id = Guid.NewGuid();
        Source = source;
        Reference = reference;
        AddedAt = addedAt;
        AddedBy = addedBy;
    }

    public Guid Id { get; private set; }

    /// <summary>Host and path, lower case, as <see cref="BlockedUrls.Normalise"/> gives it.</summary>
    public string Pattern { get; private set; }

    public string Source { get; private set; }

    public string? Reference { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    public Guid? AddedBy { get; private set; }
}
