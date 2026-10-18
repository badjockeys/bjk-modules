using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;

namespace BJK.Modules.User.UseCases;

internal static class UserAccess
{
    public static async Task<UserAccount> GetMineAsync(
        IUserRepository users,
        ICurrentUser caller,
        CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        return await users.GetByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("User", userId);
    }

    /// <summary>Any account, for a moderator or admin.</summary>
    public static async Task<UserAccount> GetForModeratorAsync(
        IUserRepository users,
        ICurrentUser caller,
        Guid userId,
        CancellationToken cancellationToken)
    {
        caller.RequireModerator(UserModule.ManageScope);
        return await users.GetByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("User", userId);
    }

    /// <summary>
    /// Moderators can act on ordinary users. Accounts with a staff role (moderators and admins) can only be
    /// changed by an admin, so a moderator can't remove another moderator.
    /// </summary>
    public static async Task EnsureCanActOnAsync(
        IIdentityService identity,
        ICurrentUser caller,
        UserAccount target,
        CancellationToken cancellationToken)
    {
        var signIn = await identity.GetAccountAsync(target.Id.ToString("D"), cancellationToken);
        if (signIn is { IsStaff: true })
        {
            caller.RequireAdmin(UserModule.ManageScope);
        }
    }
}
