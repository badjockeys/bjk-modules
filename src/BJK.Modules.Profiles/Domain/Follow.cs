namespace BJK.Modules.Profiles.Domain;

/// <summary>
/// One user following another, in <c>profiles.Follows</c>. Their posts show in the follower's Following timeline.
/// Blocking either way removes the follow and prevents a new one.
/// </summary>
public sealed class Follow
{
    /// <summary>Keeps the Following timeline quick; X's limit is similar.</summary>
    public const int MaxPerUser = 5000;

    // For EF Core.
    private Follow()
    {
    }

    public Guid FollowerId { get; private set; }

    public Guid FolloweeId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Follow Create(Guid followerId, Guid followeeId, DateTimeOffset now)
    {
        if (followerId == Guid.Empty || followeeId == Guid.Empty)
        {
            throw new ArgumentException("Both user ids are required.");
        }

        if (followerId == followeeId)
        {
            throw new ArgumentException("You can't follow yourself.", nameof(followeeId));
        }

        return new Follow { FollowerId = followerId, FolloweeId = followeeId, CreatedAt = now };
    }
}
