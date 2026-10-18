namespace BJK.Modules.Shared.Security;

/// <summary>
/// The authentication scheme every module API uses.
/// Each host registers its bearer token handler under this name:
/// the identity server uses its local API handler, other hosts use JWT bearer.
/// </summary>
public static class ModuleAuthentication
{
    public const string Scheme = "BjkApi";
}
