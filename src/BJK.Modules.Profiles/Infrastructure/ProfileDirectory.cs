using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases;

namespace BJK.Modules.Profiles.Infrastructure;

internal sealed class ProfileDirectory(IProfileRepository profiles, IMediaDirectory media) : IProfileDirectory
{
    public async Task<bool> ExistsAsync(Guid userId, CancellationToken cancellationToken) =>
        await profiles.GetByIdAsync(userId, cancellationToken) is { IsActive: true };

    public async Task<IReadOnlyDictionary<Guid, ProfileSummary>> GetSummariesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, ProfileSummary>();
        }

        var found = (await profiles.GetManyAsync(userIds, cancellationToken)).Where(p => p.IsActive).ToList();
        var avatars = await Avatars.GetUrlsAsync(media, found, cancellationToken);

        return found.ToDictionary(
            p => p.Id,
            p => new ProfileSummary(p.Id, p.Handle, p.DisplayName, Avatars.UrlFor(p, avatars), p.CreatedAt));
    }

    public async Task<ProfileSummary?> FindByHandleAsync(string handle, CancellationToken cancellationToken)
    {
        var normalised = Handle.Normalise(handle);
        if (!Handle.IsValid(normalised))
        {
            return null;
        }

        var profile = await profiles.GetByHandleAsync(normalised, cancellationToken);
        if (profile is not { IsActive: true })
        {
            return null;
        }

        var avatars = await Avatars.GetUrlsAsync(media, [profile], cancellationToken);
        return new ProfileSummary(profile.Id, profile.Handle, profile.DisplayName, Avatars.UrlFor(profile, avatars), profile.CreatedAt);
    }
}

internal sealed class BlockDirectory(IProfileRepository profiles) : IBlockDirectory
{
    public async Task<IReadOnlyCollection<Guid>> GetBlockedIdsAsync(Guid blockerId, CancellationToken cancellationToken) =>
        await profiles.GetBlockedIdsAsync(blockerId, cancellationToken);

    public Task<bool> IsBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken) =>
        profiles.IsBlockedAsync(blockerId, blockedId, cancellationToken);
}

internal sealed class ProfileModeration(IProfileRepository profiles, IMediaDirectory media, TimeProvider clock) : IProfileModeration
{
    public async Task<bool> ResetPublicDetailsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetByIdAsync(userId, cancellationToken);
        if (profile is null)
        {
            return false;
        }

        var oldAvatar = profile.ResetPublicDetails(clock.GetUtcNow());
        await profiles.SaveChangesAsync(cancellationToken);
        if (oldAvatar is { } avatarId)
        {
            await media.DeleteAsync([avatarId], cancellationToken);
        }

        return true;
    }
}
