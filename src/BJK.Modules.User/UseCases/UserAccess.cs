using BJK.Modules.Shared.Exceptions;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;

namespace BJK.Modules.User.UseCases;

internal static class UserAccess
{
    /// <summary>Loads a user and checks the caller owns it. Admin overrides can be added here later.</summary>
    public static async Task<UserProfile> GetOwnedAsync(
        IUserRepository users,
        Guid userId,
        string callerIdentityId,
        CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("User", userId);

        if (!user.IsOwnedBy(callerIdentityId))
        {
            throw new ForbiddenException("You can only change your own account.");
        }

        return user;
    }
}
