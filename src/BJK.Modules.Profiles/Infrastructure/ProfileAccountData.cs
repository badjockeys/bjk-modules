using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;

namespace BJK.Modules.Profiles.Infrastructure;

/// <summary>Hides the profile while the account is deactivated, shows it again on return, and deletes it with the account.</summary>
internal sealed class ProfilesUserLifecycleHandler(IProfileRepository profiles, TimeProvider clock) : IUserLifecycleHandler
{
    public async Task HandleAsync(UserLifecycleEvent userEvent, CancellationToken cancellationToken)
    {
        if (userEvent.Type == UserLifecycleEventType.Deleted)
        {
            // Blocks and follows can exist without a profile on either side, so they go first.
            await profiles.DeleteBlocksForUserAsync(userEvent.UserId, cancellationToken);
            await profiles.DeleteFollowsForUserAsync(userEvent.UserId, cancellationToken);
        }

        var profile = await profiles.GetByIdAsync(userEvent.UserId, cancellationToken);
        if (profile is null)
        {
            return;
        }

        switch (userEvent.Type)
        {
            case UserLifecycleEventType.Deactivated:
                profile.Deactivate(clock.GetUtcNow());
                break;
            case UserLifecycleEventType.Reactivated:
                profile.Reactivate();
                break;
            case UserLifecycleEventType.Deleted:
                // The avatar's files go with the rest of the user's media (Media module).
                profiles.Remove(profile);
                break;
        }

        await profiles.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class ProfilePersonalDataExporter(IProfileRepository profiles) : IPersonalDataExporter
{
    public string Section => "profile";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var p = await profiles.GetByIdAsync(userId, cancellationToken);
        var blocks = (await profiles.ListBlocksAsync(userId, cancellationToken))
            .Select(b => new { b.BlockedId, b.CreatedAt })
            .ToList();
        var following = await profiles.GetFollowingIdsAsync(userId, cancellationToken);
        var badges = (await profiles.ListBadgesAsync(userId, cancellationToken)).Select(b => b.ToString()).ToList();
        return p is null
            ? (object)new { blocks, following }
            : new
            {
                p.Id,
                p.Handle,
                p.DisplayName,
                p.Bio,
                p.Location,
                p.AvatarMediaId,
                p.CreatedAt,
                p.UpdatedAt,
                p.DeactivatedAt,
                p.IsMinor,
                blocks,
                following,
                badges,
            };
    }
}
