using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.User.Contracts;

/// <summary>
/// For the identity server's token issuing: has this user passed the age check? Tokens carry
/// <see cref="AgeVerificationClaims.AgeVerified"/> only when they have.
/// </summary>
public interface IAgeVerificationLookup
{
    /// <summary>True if the account exists and has passed the age check.</summary>
    /// <param name="userId">The user's id (the token's "sub").</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<bool> IsAgeVerifiedAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>The token claim for a passed age check, shared with every module that requires it.</summary>
public static class AgeVerificationClaims
{
    /// <summary>Claim type. Present, with value <see cref="Verified"/>, only once the account passes the age check.</summary>
    public const string AgeVerified = "age_verified";

    /// <summary>The claim's value.</summary>
    public const string Verified = "true";
}

internal sealed class AgeVerificationLookup(UserDbContext db) : IAgeVerificationLookup
{
    public Task<bool> IsAgeVerifiedAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Accounts.AsNoTracking().AnyAsync(a => a.Id == userId && a.AgeCheckStatus == AgeCheckStatus.Passed, cancellationToken);
}
