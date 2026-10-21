namespace BJK.Modules.Profiles.Domain;

/// <summary>
/// Badges on a profile (owner, 21 Oct 2026). They reward quality, never posting volume: rewarding volume would
/// encourage people to go looking for incidents.
/// </summary>
public enum Badge
{
    /// <summary>Among the first <see cref="ProfileBadge.FoundingMemberLimit"/> members. Given when the profile is made.</summary>
    FoundingMember,

    /// <summary>Given by a moderator: several posts, none removed, no upheld reports.</summary>
    TrustedReporter,

    /// <summary>
    /// Posted in their home area in at least 3 different months of the last 6. Given automatically; not shown on
    /// under-18s' profiles, because it would hint at where they live.
    /// </summary>
    LocalRegular,
}

/// <summary>A badge someone holds. Key: user and badge. <c>AwardedBy</c> is the moderator, or null when automatic.</summary>
public sealed class ProfileBadge
{
    /// <summary>How many members get the Founding member badge.</summary>
    public const int FoundingMemberLimit = 1000;

    // For EF Core.
    private ProfileBadge()
    {
    }

    public Guid UserId { get; private set; }

    public Badge Badge { get; private set; }

    public DateTimeOffset AwardedAt { get; private set; }

    public Guid? AwardedBy { get; private set; }

    public static ProfileBadge Award(Guid userId, Badge badge, DateTimeOffset now, Guid? awardedBy = null) =>
        new() { UserId = userId, Badge = badge, AwardedAt = now, AwardedBy = awardedBy };

    /// <summary>Badges shown on a profile: all of them, except Local regular for under-18s (or unknown age).</summary>
    public static IReadOnlyList<Badge> Visible(IEnumerable<Badge> badges, bool? isMinor) =>
        badges.Where(b => b != Badge.LocalRegular || isMinor == false).Order().ToList();
}
