using System.Security.Claims;

namespace BJK.Modules.Shared.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The caller's identity server account id (the token's "sub" claim), or null if anonymous.</summary>
    public static string? GetIdentityId(this ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    /// <summary>
    /// True if the access token carries the scope. Handles scopes issued either as
    /// one claim per scope or as a single space-delimited claim.
    /// </summary>
    public static bool HasScope(this ClaimsPrincipal principal, string scope) =>
        principal.Identity?.IsAuthenticated == true
        && principal.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);

    /// <summary>True if the token carries the role (as "role" or the .NET role claim type).</summary>
    public static bool HasRole(this ClaimsPrincipal principal, string role) =>
        principal.Identity?.IsAuthenticated == true
        && (principal.HasClaim("role", role) || principal.HasClaim(ClaimTypes.Role, role));

    /// <summary>True if the user signed in with a second factor (the token's "amr" includes "mfa").</summary>
    public static bool UsedMfa(this ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && (principal.HasClaim("amr", "mfa") || principal.HasClaim(AuthenticationMethodsClaimType, "mfa"));

    /// <summary>True if the user has confirmed their email address (the token's "email_verified").</summary>
    public static bool IsEmailVerified(this ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if the user has passed the age check (18 and over): the token's "age_verified" claim is "true". The
    /// identity server issues it once the account passes the on-demand check; it's what viewing sensitive posts
    /// needs. Posting, commenting and reacting only need it if <c>Safety:RequireAgeVerification</c> is turned on.
    /// </summary>
    public static bool IsAgeVerified(this ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && string.Equals(principal.FindFirstValue(AgeVerifiedClaimType), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if the token says the user is under 18 by their declared date of birth (the "minor" claim is "true").
    /// The identity server works it out each time it issues a token, so it lapses on the 18th birthday.
    /// </summary>
    public static bool IsMinor(this ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && string.Equals(principal.FindFirstValue(MinorClaimType), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The token's "email" claim, if it has one.</summary>
    public static string? GetEmail(this ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
            ? principal.FindFirstValue("email") ?? principal.FindFirstValue(ClaimTypes.Email)
            : null;

    /// <summary>
    /// Moderator rights for a module: the token has the module's manage scope, and either it's a signed-in
    /// Moderator or Admin who used two-factor sign-in, or it's a machine token (no user) issued to a trusted client.
    /// </summary>
    public static bool HasModeratorRights(this ClaimsPrincipal principal, string manageScope) =>
        principal.HasScope(manageScope)
        && (principal.GetIdentityId() is null
            || ((principal.HasRole(StaffRoles.Moderator) || principal.HasRole(StaffRoles.Admin)) && principal.UsedMfa()));

    /// <summary>Admin rights: like <see cref="HasModeratorRights"/>, but only the Admin role.</summary>
    public static bool HasAdminRights(this ClaimsPrincipal principal, string manageScope) =>
        principal.HasScope(manageScope)
        && (principal.GetIdentityId() is null || (principal.HasRole(StaffRoles.Admin) && principal.UsedMfa()));

    /// <summary>The access-token claim for a passed age check.</summary>
    public const string AgeVerifiedClaimType = "age_verified";

    /// <summary>The access-token claim for an under-18 account (by declared date of birth).</summary>
    public const string MinorClaimType = "minor";

    // How Microsoft's JWT handler renames "amr" when it maps inbound claims.
    private const string AuthenticationMethodsClaimType = "http://schemas.microsoft.com/claims/authnmethodsreferences";
}
