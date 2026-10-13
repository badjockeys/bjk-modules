namespace BJK.Modules.User.Infrastructure.Services;

/// <summary>Calls into bjk-identity-server for things the User module doesn't own, such as credentials.</summary>
public interface IIdentityService
{
    /// <summary>Asks the identity server to send the account's owner a password reset email.</summary>
    Task SendPasswordResetAsync(string identityId, CancellationToken cancellationToken);
}
