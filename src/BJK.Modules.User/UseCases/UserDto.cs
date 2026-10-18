using BJK.Modules.User.Domain;

namespace BJK.Modules.User.UseCases;

public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    internal static UserDto From(UserProfile user) =>
        new(user.Id, user.Email, user.DisplayName, user.CreatedAt, user.UpdatedAt);
}
