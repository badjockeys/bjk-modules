namespace BJK.Modules.Posts.Domain;

/// <summary>Where a passenger tag stands. Stored by name.</summary>
public enum PassengerTagStatus
{
    /// <summary>Waiting for the tagged person to accept: their name isn't shown on the post yet.</summary>
    Pending,

    /// <summary>They accepted: they're shown on the post as a passenger.</summary>
    Accepted,

    /// <summary>They declined. Kept so they can't be tagged on the same post again.</summary>
    Declined,
}

/// <summary>
/// Someone the author says was in their vehicle, stored in <c>posts.PostPassengers</c>. Only on driving incidents
/// (any type but bad parking), up to <see cref="MaxPerPost"/>, only adults, and their name only shows once they accept.
/// </summary>
public sealed class PostPassenger
{
    /// <summary>The most passengers a post can have.</summary>
    public const int MaxPerPost = 4;

    // For EF Core.
    private PostPassenger()
    {
    }

    public Guid PostId { get; private set; }

    /// <summary>The tagged person's user id.</summary>
    public Guid UserId { get; private set; }

    public PassengerTagStatus Status { get; private set; }

    public DateTimeOffset TaggedAt { get; private set; }

    public DateTimeOffset? RespondedAt { get; private set; }

    public static PostPassenger Tag(Guid postId, Guid userId, DateTimeOffset now) =>
        postId == Guid.Empty || userId == Guid.Empty
            ? throw new ArgumentException("A post and a user are required.")
            : new PostPassenger { PostId = postId, UserId = userId, Status = PassengerTagStatus.Pending, TaggedAt = now };

    /// <summary>
    /// The tagged person accepts or declines. A declined tag stays declined (they can remove themselves from an
    /// accepted one instead, which deletes it). Returns true if it changed.
    /// </summary>
    public bool Respond(bool accept, DateTimeOffset now)
    {
        var status = accept ? PassengerTagStatus.Accepted : PassengerTagStatus.Declined;
        if (Status == status || (Status == PassengerTagStatus.Declined && accept))
        {
            return false;
        }

        Status = status;
        RespondedAt = now;
        return true;
    }

    /// <summary>Passengers can only be tagged on driving incidents: a post with any type but bad parking.</summary>
    public static bool AllowedFor(IEnumerable<IncidentCategory> types) => types.Any(IncidentTypeFlags.IsOnTheRoad);
}
