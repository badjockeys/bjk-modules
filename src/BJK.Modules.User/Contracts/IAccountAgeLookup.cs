using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.User.Contracts;

/// <summary>
/// For the identity server's sign-in pages and token issuing: has the account finished sign-up (date of birth and
/// liveness), is it a minor's, and has it passed the age check? Read fresh every time a token is issued, so the
/// <c>minor</c> claim lapses on the 18th birthday.
/// </summary>
public interface IAccountAgeLookup
{
    /// <summary>The account's age facts, or null if there's no account record.</summary>
    /// <param name="userId">The user's id (the token's "sub").</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<AccountAgeFacts?> GetAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>What token issuing needs to know about an account's age. Never logged.</summary>
/// <param name="DateOfBirth">The declared date of birth (null only for accounts created before it was asked for).</param>
/// <param name="LivenessPassed">True once the sign-up liveness check has passed.</param>
/// <param name="AgeVerified">True once the age check (18+, for sensitive posts) has passed.</param>
public sealed record AccountAgeFacts(DateOnly? DateOfBirth, bool LivenessPassed, bool AgeVerified)
{
    /// <summary>Date of birth recorded and liveness passed: the account may get tokens.</summary>
    public bool IsSignUpComplete => DateOfBirth is not null && LivenessPassed;

    /// <summary>True if the declared date of birth says the user is under 18 on <paramref name="today"/>.</summary>
    /// <param name="today">Today (UTC date).</param>
    public bool IsMinorOn(DateOnly today) => DateOfBirth is { } dateOfBirth && AgeRules.IsMinor(dateOfBirth, today);

    /// <summary>Never prints the date of birth.</summary>
    public override string ToString() => $"{nameof(AccountAgeFacts)}(signUpComplete: {IsSignUpComplete}, ageVerified: {AgeVerified})";
}

/// <summary>The token claims about age, shared with every module that reads them.</summary>
public static class AgeVerificationClaims
{
    /// <summary>Claim type. Present, with value <see cref="Verified"/>, only once the account passes the age check.</summary>
    public const string AgeVerified = ClaimsPrincipalExtensions.AgeVerifiedClaimType;

    /// <summary>
    /// Claim type. Present, with value <see cref="Verified"/> ("true"), while the declared date of birth says the user
    /// is under 18. Worked out each time a token is issued, so it disappears on the 18th birthday.
    /// </summary>
    public const string Minor = ClaimsPrincipalExtensions.MinorClaimType;

    /// <summary>The claims' value.</summary>
    public const string Verified = "true";
}

internal sealed class AccountAgeLookup(UserDbContext db) : IAccountAgeLookup
{
    public async Task<AccountAgeFacts?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var row = await db.Accounts.AsNoTracking()
            .Where(a => a.Id == userId)
            .Select(a => new { a.DateOfBirth, a.LivenessStatus, a.AgeCheckStatus })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new AccountAgeFacts(row.DateOfBirth, row.LivenessStatus == LivenessStatus.Passed, row.AgeCheckStatus == AgeCheckStatus.Passed);
    }
}
