namespace BJK.Modules.User.API.Contracts;

/// <summary>Sign-up. People aged 13 and over can join; under 13 is refused (400, error code <c>too_young</c>) and nothing is kept.</summary>
/// <param name="Email">The email address to sign in with.</param>
/// <param name="Password">The password.</param>
/// <param name="DateOfBirth">The date of birth, e.g. "2010-05-31".</param>
public sealed record AddUserRequest(string Email, string Password, DateOnly? DateOfBirth);

public sealed record DeactivateAccountRequest(string Password);

public sealed record ReactivateAccountRequest(string Email, string Password);

public sealed record RequestPasswordResetRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record CreatedUserResponse(Guid Id);

public sealed record ConfirmEmailRequest(Guid UserId, string Token);

public sealed record SetRolesRequest(IReadOnlyList<string> Roles);
