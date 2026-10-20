namespace BJK.Modules.Profiles.Contracts;

/// <summary>What other modules (Posts) may ask of the Profiles module, for example to show who wrote a post.</summary>
public interface IProfileDirectory
{
    Task<bool> ExistsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Summaries keyed by user id. Users without a profile are left out.</summary>
    Task<IReadOnlyDictionary<Guid, ProfileSummary>> GetSummariesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken);

    /// <summary>Finds a profile by handle, with or without the @ and in any case.</summary>
    Task<ProfileSummary?> FindByHandleAsync(string handle, CancellationToken cancellationToken);
}

/// <summary>
/// A profile as other modules see it. <c>CreatedAt</c> lets Posts apply the new-account limits. <c>IsAdult</c> is
/// true only if the user's own token last said they're 18 or over (false if under 18 or not known yet): Posts only
/// lets adults be tagged. <c>RoadUser</c> is "Driver", "Cyclist" or "Both" only if they chose to show it.
/// </summary>
public sealed record ProfileSummary(
    Guid UserId,
    string Handle,
    string DisplayName,
    Uri? AvatarUrl,
    DateTimeOffset CreatedAt = default,
    bool IsAdult = false,
    string? RoadUser = null);

/// <summary>Blocks between users, for Posts to hide content and refuse interactions.</summary>
public interface IBlockDirectory
{
    /// <summary>Everyone the user has blocked. Their posts and comments are hidden from the user.</summary>
    Task<IReadOnlyCollection<Guid>> GetBlockedIdsAsync(Guid blockerId, CancellationToken cancellationToken);

    /// <summary>
    /// Everyone who has blocked the user, and everyone the user has blocked: people whose content is left out of the
    /// user's reels either way.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetBlockedEitherWayAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>True if <paramref name="blockerId"/> has blocked <paramref name="blockedId"/>.</summary>
    Task<bool> IsBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken);
}

/// <summary>Moderation of profiles by other modules (Posts resolves reports about profiles).</summary>
public interface IProfileModeration
{
    /// <summary>
    /// Resets the public details the user wrote (display name back to the handle; bio, location and avatar removed)
    /// and deletes the old avatar. Returns false if there's no profile.
    /// </summary>
    Task<bool> ResetPublicDetailsAsync(Guid userId, CancellationToken cancellationToken);
}
