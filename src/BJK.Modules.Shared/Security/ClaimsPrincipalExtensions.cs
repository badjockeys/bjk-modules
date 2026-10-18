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
}
