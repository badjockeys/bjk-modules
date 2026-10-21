namespace BJK.Modules.Posts.Domain;

/// <summary>
/// Which question a post's vote asks (owner, 21 Oct 2026). Incidents between road users ask "Who's at fault?"; posts
/// about one driver (bad parking, phone use, speeding, red lights, lane hogging, other) ask "How bad was it?".
/// </summary>
public enum VoteKind
{
    /// <summary>"Who's at fault?": the filming driver, the other road user, both, or nobody.</summary>
    Fault,

    /// <summary>"How bad was it?": minor, careless, dangerous or shocking.</summary>
    Severity,
}

/// <summary>An answer to a post's vote. The first four answer <see cref="VoteKind.Fault"/>, the rest <see cref="VoteKind.Severity"/>.</summary>
public enum VoteChoice
{
    /// <summary>The driver who filmed it was at fault.</summary>
    FilmingDriver,

    /// <summary>The other road user was at fault.</summary>
    OtherRoadUser,

    /// <summary>Both were at fault.</summary>
    Both,

    /// <summary>Nobody was really at fault.</summary>
    Nobody,

    /// <summary>Minor: annoying but not risky.</summary>
    Minor,

    /// <summary>Careless driving.</summary>
    Careless,

    /// <summary>Dangerous driving.</summary>
    Dangerous,

    /// <summary>Shocking: about as bad as it gets.</summary>
    Shocking,
}

/// <summary>When the vote on an author's posts closes. A setting each author chooses for their own posts.</summary>
public enum VoteDuration
{
    /// <summary>48 hours after posting.</summary>
    Hours48,

    /// <summary>7 days after posting (the default).</summary>
    Days7,

    /// <summary>The vote never closes.</summary>
    Never,
}

/// <summary>The vote rules: which kind a post gets, which answers fit it, and when it closes.</summary>
public static class PostVoting
{
    /// <summary>Types where two road users interact, so "Who's at fault?" makes sense.</summary>
    public static readonly IReadOnlySet<IncidentCategory> InteractionTypes = new HashSet<IncidentCategory>
    {
        IncidentCategory.RoadRage,
        IncidentCategory.DangerousOvertaking,
        IncidentCategory.Tailgating,
        IncidentCategory.CuttingUp,
        IncidentCategory.Undertaking,
        IncidentCategory.ClosePass,
    };

    /// <summary>Results stay hidden from signed-out visitors until a post has this many votes.</summary>
    public const int PublicResultsAfter = 10;

    /// <summary>A post with any interaction type gets the fault vote; every other post gets the severity vote.</summary>
    public static VoteKind KindFor(IEnumerable<IncidentCategory> types) =>
        types.Any(InteractionTypes.Contains) ? VoteKind.Fault : VoteKind.Severity;

    /// <summary>The answers that fit a kind of vote.</summary>
    public static IReadOnlyList<VoteChoice> ChoicesFor(VoteKind kind) => kind == VoteKind.Fault
        ? [VoteChoice.FilmingDriver, VoteChoice.OtherRoadUser, VoteChoice.Both, VoteChoice.Nobody]
        : [VoteChoice.Minor, VoteChoice.Careless, VoteChoice.Dangerous, VoteChoice.Shocking];

    /// <summary>When a vote closes for a post created at <paramref name="createdAt"/>; null when it never does.</summary>
    public static DateTimeOffset? ClosesAt(DateTimeOffset createdAt, VoteDuration duration) => duration switch
    {
        VoteDuration.Hours48 => createdAt.AddHours(48),
        VoteDuration.Never => null,
        _ => createdAt.AddDays(7),
    };
}

/// <summary>One person's answer to a post's vote. They can change it until the vote closes. Key: post and user.</summary>
public sealed class PostVote
{
    // For EF Core.
    private PostVote()
    {
    }

    public Guid PostId { get; private set; }

    public Guid UserId { get; private set; }

    public VoteChoice Choice { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public static PostVote Create(Guid postId, Guid userId, VoteChoice choice, DateTimeOffset now) =>
        new() { PostId = postId, UserId = userId, Choice = choice, CreatedAt = now };

    public void Change(VoteChoice choice, DateTimeOffset now)
    {
        Choice = choice;
        UpdatedAt = now;
    }
}

/// <summary>
/// Someone following a post: they're told about new comments and the vote result. Authors follow their own posts,
/// commenting follows a post, and anyone can follow or unfollow from the post. Key: post and user.
/// </summary>
public sealed class PostFollow
{
    // For EF Core.
    private PostFollow()
    {
    }

    public Guid PostId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static PostFollow Create(Guid postId, Guid userId, DateTimeOffset now) =>
        new() { PostId = postId, UserId = userId, CreatedAt = now };
}

/// <summary>A post someone saved to come back to. Private to them. Key: user and post.</summary>
public sealed class Bookmark
{
    // For EF Core.
    private Bookmark()
    {
    }

    public Guid UserId { get; private set; }

    public Guid PostId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Bookmark Create(Guid userId, Guid postId, DateTimeOffset now) =>
        new() { UserId = userId, PostId = postId, CreatedAt = now };
}

/// <summary>
/// One viewer having seen a post, counted once per post however often they look. The viewer is the same key reels
/// use (<see cref="ReelViewer"/>): a member's id, or a keyed hash for a signed-out visitor, so no identity is kept.
/// <c>IsLocal</c>: the viewer's home area was within <see cref="LocalKm"/> of where it happened. Key: post and viewer.
/// </summary>
public sealed class PostView
{
    /// <summary>How close a viewer's home area must be to the incident for the view to count as local.</summary>
    public const int LocalKm = 10;

    // For EF Core.
    private PostView()
    {
    }

    public Guid PostId { get; private set; }

    public Guid ViewerKey { get; private set; }

    public bool IsLocal { get; private set; }

    public DateTimeOffset ViewedAt { get; private set; }

    public static PostView Create(Guid postId, Guid viewerKey, bool isLocal, DateTimeOffset now) =>
        new() { PostId = postId, ViewerKey = viewerKey, IsLocal = isLocal, ViewedAt = now };
}

/// <summary>A post being shared (the share sheet used, or the link copied), counted once per viewer per UK day.</summary>
public sealed class PostShare
{
    // For EF Core.
    private PostShare()
    {
    }

    public Guid PostId { get; private set; }

    public Guid ViewerKey { get; private set; }

    public DateOnly Day { get; private set; }

    public DateTimeOffset SharedAt { get; private set; }

    public static PostShare Create(Guid postId, Guid viewerKey, DateOnly day, DateTimeOffset now) =>
        new() { PostId = postId, ViewerKey = viewerKey, Day = day, SharedAt = now };
}

/// <summary>An author's settings for their own posts. A row exists only once they change something.</summary>
public sealed class AuthorPreferences
{
    // For EF Core.
    private AuthorPreferences()
    {
    }

    public Guid UserId { get; private set; }

    /// <summary>When the vote on their new posts closes. Changing it doesn't change posts already made.</summary>
    public VoteDuration VoteDuration { get; private set; } = VoteDuration.Days7;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AuthorPreferences Create(Guid userId, DateTimeOffset now) =>
        new() { UserId = userId, UpdatedAt = now };

    public void SetVoteDuration(VoteDuration duration, DateTimeOffset now)
    {
        VoteDuration = duration;
        UpdatedAt = now;
    }
}
