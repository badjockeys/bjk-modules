namespace BJK.Modules.User.API.Contracts;

public sealed record AddUserRequest(string Email, string Password);

public sealed record DeactivateAccountRequest(string Password);

public sealed record ReactivateAccountRequest(string Email, string Password);

public sealed record RequestPasswordResetRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record CreatedUserResponse(Guid Id);

public sealed record ConfirmEmailRequest(Guid UserId, string Token);

public sealed record SetRolesRequest(IReadOnlyList<string> Roles);
