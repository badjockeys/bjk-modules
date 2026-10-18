namespace BJK.Modules.User.API.Contracts;

public sealed record AddUserRequest(string Email, string Password, string DisplayName);

public sealed record UpdateUserRequest(string DisplayName);

public sealed record RequestPasswordResetRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record CreatedUserResponse(Guid Id);
