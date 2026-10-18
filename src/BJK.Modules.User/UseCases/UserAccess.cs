using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;

namespace BJK.Modules.User.UseCases;

internal static class UserAccess
{
    /// <summary>
    /// Loads a user the caller is allowed to see or change: their own profile,
    /// or anyone's if the token has the manage scope.
    /// </summary>
    public static async Task<UserProfile> GetAccessibleAsync(
        IUserRepository users,
        ICurrentUser caller,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("User", userId);

        var isOwner = caller.IdentityId is not null && user.IsOwnedBy(caller.IdentityId);
        if (!isOwner && !caller.HasScope(UserModule.ManageScope))
        {
            throw new ForbiddenException("You can only access your own account.");
        }

        return user;
    }
}
