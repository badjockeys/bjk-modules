namespace BJK.Modules.Profiles.Domain;

/// <summary>
/// One user blocking another, in <c>profiles.Blocks</c>. The blocker no longer sees the blocked user's posts and
/// comments, and the blocked user can't comment on or react to the blocker's posts.
/// </summary>
public sealed class Block
{
    /// <summary>Keeps feed filtering quick; nobody realistically needs more.</summary>
    public const int MaxPerUser = 1000;

    // For EF Core.
    private Block()
    {
    }

    public Guid BlockerId { get; private set; }

    public Guid BlockedId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Block Create(Guid blockerId, Guid blockedId, DateTimeOffset now)
    {
        if (blockerId == Guid.Empty || blockedId == Guid.Empty)
        {
            throw new ArgumentException("Both user ids are required.");
        }

        if (blockerId == blockedId)
        {
            throw new ArgumentException("You can't block yourself.", nameof(blockedId));
        }

        return new Block { BlockerId = blockerId, BlockedId = blockedId, CreatedAt = now };
    }
}
