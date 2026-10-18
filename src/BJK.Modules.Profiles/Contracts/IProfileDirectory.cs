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

public sealed record ProfileSummary(Guid UserId, string Handle, string DisplayName, Uri? AvatarUrl);
