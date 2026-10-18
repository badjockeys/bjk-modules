namespace BJK.Modules.User.Infrastructure.Services;

/// <summary>
/// Credentials and sign-in accounts, which the identity server owns.
/// The User module calls this; the hosting identity server implements it (with ASP.NET Core Identity).
/// </summary>
public interface IIdentityService
{
    /// <summary>Creates a sign-in account. On success, <see cref="AccountResult.IdentityId"/> is the new account id.</summary>
    Task<AccountResult> CreateAccountAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>Permanently removes an account. Used to undo a registration that failed part-way.</summary>
    Task DeleteAccountAsync(string identityId, CancellationToken cancellationToken);

    /// <summary>Stops an account from signing in or refreshing tokens, keeping its data.</summary>
    Task DisableAccountAsync(string identityId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a password reset token to the account's owner. Does nothing (and doesn't say so)
    /// if no active account has that email, so callers can't probe for registered addresses.
    /// </summary>
    Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken);

    /// <summary>Sets a new password using a token from <see cref="RequestPasswordResetAsync"/>.</summary>
    Task<AccountResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken);
}

/// <summary>Outcome of an account operation. <see cref="Errors"/> is empty on success.</summary>
public sealed record AccountResult(string? IdentityId, IReadOnlyList<AccountError> Errors)
{
    public bool Succeeded => Errors.Count == 0;

    public static AccountResult Success(string? identityId = null) => new(identityId, []);

    public static AccountResult Failed(IEnumerable<AccountError> errors) => new(null, errors.ToList());
}

/// <summary>A problem with one field of the request, e.g. ("Password", "Passwords must have at least one digit.").</summary>
public sealed record AccountError(string Field, string Message);
