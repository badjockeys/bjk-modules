namespace BJK.Modules.Shared.Security;

/// <summary>
/// The authentication scheme every module API uses.
/// Each host registers its bearer token handler under this name:
/// the identity server uses its local API handler, other hosts use JWT bearer.
/// </summary>
public static class ModuleAuthentication
{
    public const string Scheme = "BjkApi";

    /// <summary>Policy for endpoints any signed-in user may call about themselves, whatever the scopes.</summary>
    public const string SignedInUserPolicy = "bjk.signed-in-user";

    /// <summary>
    /// Route prefix for moderator and admin endpoints in every module. In production Cloudflare Access only lets the
    /// back-office reach these paths, on top of the role checks.
    /// </summary>
    public const string AdminRoutePrefix = "api/admin";
}
