using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;

namespace BJK.Modules.User.UseCases;

internal static class UserAccess
{
    public static bool IsAdmin(ICurrentUser caller) => caller.HasScope(UserModule.ManageScope);

    /// <summary>The caller's own account, or anyone's with the manage scope.</summary>
    public static async Task<UserAccount> GetAccessibleAsync(
        IUserRepository users,
        ICurrentUser caller,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var account = await users.GetByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("User", userId);

        var isOwner = caller.UserId() is { } callerId && account.IsOwnedBy(callerId);
        if (!isOwner && !IsAdmin(caller))
        {
            throw new ForbiddenException("You can only access your own account.");
        }

        return account;
    }

    public static async Task<UserAccount> GetMineAsync(
        IUserRepository users,
        ICurrentUser caller,
        CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        return await users.GetByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("User", userId);
    }

    public static void RequireAdmin(ICurrentUser caller)
    {
        if (!IsAdmin(caller))
        {
            throw new ForbiddenException("Only admins can do this.");
        }
    }
}
