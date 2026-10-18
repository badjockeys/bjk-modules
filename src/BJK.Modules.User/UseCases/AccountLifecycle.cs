using BJK.Modules.Shared.Users;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace BJK.Modules.User.UseCases;

/// <summary>
/// Deactivation, reactivation and permanent deletion, shared by the self-service, admin and retention paths.
/// Each change is saved together with a <see cref="UserLifecycleEvent"/> in the outbox, so the other parts of
/// Bad Jockeys (bjk-core) hide, show or delete the user's data to match.
/// </summary>
public sealed class AccountLifecycle(
    IUserRepository users,
    IIdentityService identity,
    IOptions<UserOptions> options,
    TimeProvider clock)
{
    /// <summary>Stops sign-in and starts the grace period. Safe to repeat.</summary>
    public async Task DeactivateAsync(UserAccount account, CancellationToken cancellationToken)
    {
        // Sign-in is stopped first, so a retry after a failed save still ends up consistent.
        await identity.DisableAccountAsync(account.Id.ToString("D"), cancellationToken);

        var now = clock.GetUtcNow();
        if (account.Deactivate(now, options.Value.DeactivationGracePeriod))
        {
            users.AddEvent(NewEvent(account.Id, UserLifecycleEventType.Deactivated, now));
            await users.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Allows sign-in again and cancels the deletion. Safe to repeat.</summary>
    public async Task ReactivateAsync(UserAccount account, CancellationToken cancellationToken)
    {
        await identity.EnableAccountAsync(account.Id.ToString("D"), cancellationToken);

        var now = clock.GetUtcNow();
        if (account.Reactivate(now))
        {
            users.AddEvent(NewEvent(account.Id, UserLifecycleEventType.Reactivated, now));
            await users.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Deletes the sign-in account and this record for good, and tells the other services to delete everything else.</summary>
    public async Task DeletePermanentlyAsync(UserAccount account, CancellationToken cancellationToken)
    {
        await identity.DeleteAccountAsync(account.Id.ToString("D"), cancellationToken);

        users.Remove(account);
        users.AddEvent(NewEvent(account.Id, UserLifecycleEventType.Deleted, clock.GetUtcNow()));
        await users.SaveChangesAsync(cancellationToken);
    }

    private static UserLifecycleEvent NewEvent(Guid userId, UserLifecycleEventType type, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now), userId, type, now);
}
