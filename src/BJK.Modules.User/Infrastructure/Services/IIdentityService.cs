namespace BJK.Modules.User.Infrastructure.Services;

/// <summary>
/// Credentials and sign-in accounts, which the identity server owns.
/// The User module calls this; the hosting identity server implements it (with ASP.NET Core Identity).
/// </summary>
public interface IIdentityService
{
    /// <summary>Creates a sign-in account. On success, <see cref="AccountResult.IdentityId"/> is the new account id.</summary>
    Task<AccountResult> CreateAccountAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>The sign-in details of one account, or null if it doesn't exist.</summary>
    Task<SignInAccount?> GetAccountAsync(string identityId, CancellationToken cancellationToken);

    /// <summary>Sign-in details for several accounts, keyed by id. Missing accounts are left out.</summary>
    Task<IReadOnlyDictionary<string, SignInAccount>> GetAccountsAsync(
        IReadOnlyCollection<string> identityIds,
        CancellationToken cancellationToken);

    /// <summary>True if the password is the account's current password.</summary>
    Task<bool> CheckPasswordAsync(string identityId, string password, CancellationToken cancellationToken);

    /// <summary>
    /// The id of the account with this email and password, or null. Used to reactivate a deactivated account,
    /// so it checks the password even though the account can't sign in.
    /// </summary>
    Task<string?> FindByCredentialsAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>Stops an account from signing in, and ends its sessions and refresh tokens. Keeps its data.</summary>
    Task DisableAccountAsync(string identityId, CancellationToken cancellationToken);

    /// <summary>Lets a disabled account sign in again.</summary>
    Task EnableAccountAsync(string identityId, CancellationToken cancellationToken);

    /// <summary>Permanently removes an account and its sessions and refresh tokens. Does nothing if it's already gone.</summary>
    Task DeleteAccountAsync(string identityId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a password reset token to the account's owner. Does nothing (and doesn't say so)
    /// if no active account has that email, so callers can't probe for registered addresses.
    /// </summary>
    Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken);

    /// <summary>Sets a new password using a token from <see cref="RequestPasswordResetAsync"/>.</summary>
    Task<AccountResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken);
}

/// <summary>A sign-in account, as the identity server holds it.</summary>
public sealed record SignInAccount(string Id, string Email, bool EmailConfirmed);

/// <summary>Outcome of an account operation. <see cref="Errors"/> is empty on success.</summary>
public sealed record AccountResult(string? IdentityId, IReadOnlyList<AccountError> Errors)
{
    public bool Succeeded => Errors.Count == 0;

    public static AccountResult Success(string? identityId = null) => new(identityId, []);

    public static AccountResult Failed(IEnumerable<AccountError> errors) => new(null, errors.ToList());
}

/// <summary>A problem with one field of the request, e.g. ("Password", "Passwords must have at least one digit.").</summary>
public sealed record AccountError(string Field, string Message);
