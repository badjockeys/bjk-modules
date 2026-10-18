using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;

namespace BJK.Modules.User.UseCases;

/// <summary>An account as the user (or an admin) sees it. The public profile (handle, display name) is in bjk-core.</summary>
public sealed record UserDto(
    Guid Id,
    string? Email,
    bool EmailConfirmed,
    UserAccountStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeactivatedAt,
    DateTimeOffset? DeletionDueAt)
{
    internal static UserDto From(UserAccount account, SignInAccount? signIn) => new(
        account.Id,
        signIn?.Email,
        signIn?.EmailConfirmed ?? false,
        account.Status,
        account.CreatedAt,
        account.DeactivatedAt,
        account.DeletionDueAt);
}
