using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.AgeChecks;

namespace BJK.Modules.User.UseCases;

/// <summary>
/// An account as the user (or a moderator) sees it, including the declared date of birth. The public profile (handle, display name) is in bjk-core.
/// </summary>
public sealed record UserDto(
    Guid Id,
    string? Email,
    bool EmailConfirmed,
    bool TwoFactorEnabled,
    IReadOnlyList<string> Roles,
    UserAccountStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeactivatedAt,
    DateTimeOffset? DeletionDueAt,
    AgeCheckSummaryDto AgeCheck,
    DateOnly? DateOfBirth)
{
    /// <summary>True once the account has passed the age check (tokens carry age_verified=true).</summary>
    public bool AgeVerified => AgeCheck.Status == AgeCheckStatus.Passed;

    internal static UserDto From(UserAccount account, SignInAccount? signIn) => new(
        account.Id,
        signIn?.Email,
        signIn?.EmailConfirmed ?? false,
        signIn?.TwoFactorEnabled ?? false,
        signIn?.Roles ?? [],
        account.Status,
        account.CreatedAt,
        account.DeactivatedAt,
        account.DeletionDueAt,
        AgeCheckSummaryDto.From(account),
        account.DateOfBirth);
}
