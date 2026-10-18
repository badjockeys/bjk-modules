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
}
