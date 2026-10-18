using BJK.Modules.Shared.Exceptions;

namespace BJK.Modules.Shared.Security;

public static class CurrentUserExtensions
{
    /// <summary>
    /// The caller's user id: the token's "sub", which is the same GUID in every module.
    /// Null for anonymous calls and client-only (machine) tokens.
    /// </summary>
    public static Guid? UserId(this ICurrentUser caller) =>
        Guid.TryParse(caller.IdentityId, out var id) && id != Guid.Empty ? id : null;

    /// <summary>The caller's user id, or a 403 if the call wasn't made with a signed-in user's token.</summary>
    public static Guid RequireUserId(this ICurrentUser caller) =>
        caller.UserId() ?? throw new ForbiddenException("This needs a signed-in user's token.");

    /// <summary>Moderator rights for a module; see <see cref="ClaimsPrincipalExtensions.HasModeratorRights"/>.</summary>
    public static bool HasModeratorRights(this ICurrentUser caller, string manageScope) =>
        caller.HasScope(manageScope)
        && (caller.IdentityId is null
            || ((caller.HasRole(StaffRoles.Moderator) || caller.HasRole(StaffRoles.Admin)) && caller.UsedMfa));

    /// <summary>Admin rights for a module; see <see cref="ClaimsPrincipalExtensions.HasAdminRights"/>.</summary>
    public static bool HasAdminRights(this ICurrentUser caller, string manageScope) =>
        caller.HasScope(manageScope)
        && (caller.IdentityId is null || (caller.HasRole(StaffRoles.Admin) && caller.UsedMfa));

    /// <summary>A 403 unless the caller has moderator rights for the module.</summary>
    public static void RequireModerator(this ICurrentUser caller, string manageScope)
    {
        if (!caller.HasModeratorRights(manageScope))
        {
            throw new ForbiddenException("This needs a moderator or admin who signed in with two-factor authentication.");
        }
    }

    /// <summary>A 403 unless the caller has admin rights for the module.</summary>
    public static void RequireAdmin(this ICurrentUser caller, string manageScope)
    {
        if (!caller.HasAdminRights(manageScope))
        {
            throw new ForbiddenException("This needs an admin who signed in with two-factor authentication.");
        }
    }
}
