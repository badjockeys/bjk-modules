namespace BJK.Modules.Shared.Security;

/// <summary>
/// Staff roles, held in the identity server and carried in tokens as "role" claims.
/// Moderators manage users, posts, comments and reports. Admins can do everything moderators can, and are the only
/// ones who can add or remove moderators, change roles, or permanently delete an account.
/// Both must use two-factor sign-in for their staff rights to count.
/// </summary>
public static class StaffRoles
{
    public const string Moderator = "Moderator";
    public const string Admin = "Admin";

    public static readonly IReadOnlyList<string> All = [Moderator, Admin];
}
