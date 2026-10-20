using System.Text.RegularExpressions;
using BJK.Modules.Shared.Safety;

namespace BJK.Modules.Posts.Domain;

/// <summary>
/// A hashtag (<c>posts.Hashtags</c>): one row per tag, keyed by its lower-case form, with how it was first written, when
/// it was first used and how many posts have used it. Moderators can hide a tag from Trending; it still links posts.
/// </summary>
public sealed class Hashtag
{
    /// <summary>The longest tag, without the #.</summary>
    public const int MaxLength = 50;

    // For EF Core.
    private Hashtag()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Lower case, without the # (e.g. <c>m62</c>): what makes two tags the same.</summary>
    public string Key { get; private set; } = string.Empty;

    /// <summary>As first written, without the # (e.g. <c>M62</c>).</summary>
    public string Display { get; private set; } = string.Empty;

    public DateTimeOffset FirstUsedAt { get; private set; }

    /// <summary>Posts that have used it (including ones later hidden or deleted). Suggestions are ordered by this.</summary>
    public int TotalUses { get; private set; }

    /// <summary>Hidden from Trending by a moderator (and not suggested). Its posts still link to it.</summary>
    public bool IsHiddenFromTrends { get; private set; }

    public static Hashtag Create(string display, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Key = HashtagParser.KeyOf(display),
        Display = display,
        FirstUsedAt = now,
    };

    public void Used() => TotalUses++;

    public void Unused() => TotalUses = Math.Max(0, TotalUses - 1);

    public void SetHiddenFromTrends(bool hidden) => IsHiddenFromTrends = hidden;
}

/// <summary>
/// A post's use of a hashtag (<c>posts.PostHashtags</c>), with the post's author and when it was posted, so Trending can
/// count posts and distinct authors without reading the posts' text. Comments never add hashtags.
/// </summary>
public sealed class PostHashtag
{
    // For EF Core.
    private PostHashtag()
    {
    }

    public PostHashtag(Guid postId, Guid hashtagId, Guid authorId, DateTimeOffset postedAt)
    {
        PostId = postId;
        HashtagId = hashtagId;
        AuthorId = authorId;
        PostedAt = postedAt;
    }

    public Guid PostId { get; private set; }

    public Guid HashtagId { get; private set; }

    public Guid AuthorId { get; private set; }

    public DateTimeOffset PostedAt { get; private set; }
}

/// <summary>
/// A hashtag's public posts and distinct authors in one UTC hour (<c>posts.HashtagHourlyCounts</c>). Rebuilt for the
/// last two days every few minutes (so posts hidden, held or deleted since drop out), frozen after that, and kept for
/// <c>Hashtags:HourlyRetention</c> (90 days). Trending compares the last day with the usual level from these.
/// </summary>
public sealed class HashtagHourlyCount
{
    // For EF Core.
    private HashtagHourlyCount()
    {
    }

    public Guid HashtagId { get; private set; }

    /// <summary>The start of the hour, UTC.</summary>
    public DateTimeOffset Hour { get; private set; }

    public int Posts { get; private set; }

    public int Authors { get; private set; }
}

/// <summary>Finds hashtags in a post's title and description.</summary>
public static partial class HashtagParser
{
    /// <summary>The most hashtags one post can have; any more are ignored.</summary>
    public const int MaxPerPost = 10;

    /// <summary>
    /// The tags in the text, in order, without duplicates (by lower-case key): a # at the start or after a space or
    /// punctuation (not inside a word or URL), then a letter, then up to 49 letters, digits or underscores. Tags that
    /// look like number plates or contain offensive words are left out.
    /// </summary>
    /// <param name="texts">The title and description.</param>
    public static IReadOnlyList<string> Extract(params string?[] texts)
    {
        var found = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            foreach (Match m in TagPattern().Matches(text))
            {
                var tag = m.Groups["tag"].Value;
                if (IsAllowed(tag) && keys.Add(KeyOf(tag)))
                {
                    found.Add(tag);
                    if (found.Count == MaxPerPost)
                    {
                        return found;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>The lower-case key for a tag (without the #).</summary>
    /// <param name="tag">The tag as written.</param>
    public static string KeyOf(string tag) => tag.TrimStart('#').ToLowerInvariant();

    /// <summary>
    /// Whether a tag can be used: the right shape, not a number plate (written as a tag or not), and not offensive.
    /// </summary>
    /// <param name="tag">The tag, with or without the #.</param>
    public static bool IsAllowed(string tag)
    {
        var bare = tag.TrimStart('#');
        return ShapePattern().IsMatch(bare)
            && !TextSafety.ContainsNumberPlate(bare)
            && !ScreeningKeywords.IsOffensiveTag(bare);
    }

    /// <summary>A valid key prefix for suggestions: letters, digits or underscores, starting with a letter.</summary>
    /// <param name="prefix">What has been typed after the #.</param>
    public static bool IsPrefix(string? prefix) =>
        !string.IsNullOrEmpty(prefix) && prefix.Length <= Hashtag.MaxLength && PrefixPattern().IsMatch(prefix);

    [GeneratedRegex(@"(?<![\p{L}\p{N}_/&#])#(?<tag>[A-Za-z][A-Za-z0-9_]{0,49})(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{1,49}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShapePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}
