using System.Security.Cryptography;
using System.Text;

namespace BJK.Modules.Posts.Domain;

/// <summary>
/// One reel play: a viewer watched a video post for at least 3 seconds on a UK calendar day. At most one row per
/// viewer, post and day (the key), so reporting the same play again changes nothing. Stored in
/// <c>posts.ReelPlays</c>; signed-in viewers' rows are kept for 35 days and logged-out viewers' for 2
/// (<c>Reels:PlayRetention</c>, <c>Reels:AnonymousPlayRetention</c>). Daily totals per post are kept in
/// <see cref="ReelPlayDailyCount"/>.
/// </summary>
public sealed class ReelPlay
{
    // For EF Core.
    private ReelPlay()
    {
    }

    /// <param name="viewer">Who watched.</param>
    /// <param name="day">The UK calendar day.</param>
    /// <param name="postId">The video post.</param>
    /// <param name="playedAt">When the play was reported.</param>
    public ReelPlay(ReelViewer viewer, DateOnly day, Guid postId, DateTimeOffset playedAt)
    {
        ViewerKey = viewer.Key;
        IsAnonymous = viewer.IsAnonymous;
        Day = day;
        PostId = postId;
        PlayedAt = playedAt;
    }

    /// <summary>The user id, or for a logged-out viewer a one-way hash of their viewer id (or IP address).</summary>
    public Guid ViewerKey { get; private set; }

    public DateOnly Day { get; private set; }

    public Guid PostId { get; private set; }

    /// <summary>True for a logged-out viewer (hashed key, shorter retention).</summary>
    public bool IsAnonymous { get; private set; }

    public DateTimeOffset PlayedAt { get; private set; }
}

/// <summary>How many reel plays a post had on a UK calendar day (<c>posts.ReelPlayDailyCounts</c>), for stats. Kept with the post.</summary>
public sealed class ReelPlayDailyCount
{
    // For EF Core.
    private ReelPlayDailyCount()
    {
    }

    public Guid PostId { get; private set; }

    public DateOnly Day { get; private set; }

    public int Plays { get; private set; }
}

/// <summary>
/// Who is watching reels, for the daily limits. A signed-in viewer is their user id. A logged-out viewer is the viewer
/// id the app sends (<c>X-Viewer-Id</c>: the web app's signed, httpOnly cookie, or the mobile app's install id), or,
/// without one, their IP address: a weak fallback, since many people can share an address and one person can clear
/// a cookie. Neither is stored as it is: the key is the first 16 bytes of an HMAC-SHA256 (with
/// <c>Reels:ViewerKeySecret</c>, or plain SHA-256 if that isn't set).
/// </summary>
/// <param name="Key">What plays are stored under.</param>
/// <param name="IsAnonymous">True for a logged-out viewer.</param>
public sealed record ReelViewer(Guid Key, bool IsAnonymous)
{
    /// <summary>The longest viewer id accepted; longer ones are ignored (the IP address is used instead).</summary>
    public const int MaxViewerIdLength = 128;

    /// <summary>A signed-in user.</summary>
    /// <param name="userId">The user id.</param>
    public static ReelViewer ForUser(Guid userId) => new(userId, IsAnonymous: false);

    /// <summary>
    /// A logged-out viewer, by the viewer id the app sent if it looks valid (8 to 128 letters, digits, '-', '_', '.'
    /// or ':'), otherwise by IP address. Null if there's neither.
    /// </summary>
    /// <param name="viewerId">The <c>X-Viewer-Id</c> header.</param>
    /// <param name="ipAddress">The caller's IP address.</param>
    /// <param name="secret">The HMAC key, or null/empty for plain SHA-256.</param>
    public static ReelViewer? ForAnonymous(string? viewerId, string? ipAddress, string? secret)
    {
        if (IsValidViewerId(viewerId))
        {
            return new ReelViewer(Hash("viewer:" + viewerId!.Trim(), secret), IsAnonymous: true);
        }

        return string.IsNullOrWhiteSpace(ipAddress)
            ? null
            : new ReelViewer(Hash("ip:" + ipAddress.Trim(), secret), IsAnonymous: true);
    }

    /// <summary>True if the value can be used as a viewer id.</summary>
    /// <param name="viewerId">The header value.</param>
    public static bool IsValidViewerId(string? viewerId)
    {
        var value = viewerId?.Trim();
        return value is { Length: >= 8 and <= MaxViewerIdLength }
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    }

    private static Guid Hash(string value, string? secret)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = string.IsNullOrEmpty(secret)
            ? SHA256.HashData(bytes)
            : HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes);
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// The reels ranking, "recent and popular": engagement (reactions + 2 × comments, plus 1 so a new video with no
/// engagement yet still ranks) divided by (age in hours + 2)^1.5. A post loses about two thirds of its score in its
/// first day, so a few hours' head start beats a little extra engagement, while a much-discussed clip stays up for a
/// day or two. The database computes the same formula when it refreshes <see cref="Post.ReelScore"/>.
/// </summary>
public static class ReelRanking
{
    /// <summary>The exponent of the age.</summary>
    public const double Gravity = 1.5;

    /// <summary>Hours added to the age, so brand-new posts don't divide by almost nothing.</summary>
    public const double AgeOffsetHours = 2;

    /// <param name="reactions">Reactions on the post.</param>
    /// <param name="comments">Visible comments and replies.</param>
    /// <param name="age">Time since the post was created.</param>
    public static double Score(int reactions, int comments, TimeSpan age) =>
        (1 + Math.Max(0, reactions) + (2.0 * Math.Max(0, comments)))
        / Math.Pow(Math.Max(0, age.TotalHours) + AgeOffsetHours, Gravity);
}

/// <summary>
/// UK calendar days (Europe/London, so they follow British Summer Time), for daily limits and the heat map's dates.
/// </summary>
public static class UkCalendar
{
    private static readonly Lazy<TimeZoneInfo> Zone = new(() =>
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        throw new InvalidOperationException("The Europe/London time zone isn't available (install tzdata in the container).");
    });

    /// <summary>The UK time zone.</summary>
    public static TimeZoneInfo TimeZone => Zone.Value;

    /// <summary>The UK date at an instant.</summary>
    /// <param name="instant">The instant.</param>
    public static DateOnly Today(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);

    /// <summary>When a UK day starts (midnight UK time), as an instant.</summary>
    /// <param name="day">The day.</param>
    public static DateTimeOffset StartOf(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Midnight always exists in the UK (the clocks change at 1am and 2am), so there's exactly one offset.
        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local));
    }

    /// <summary>When the next UK day starts after an instant: when daily limits reset.</summary>
    /// <param name="instant">The instant.</param>
    public static DateTimeOffset NextMidnight(DateTimeOffset instant) => StartOf(Today(instant).AddDays(1));
}
