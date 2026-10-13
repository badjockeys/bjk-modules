namespace BJK.Modules.User.API.Contracts;

public sealed record AddUserRequest(string DisplayName);

public sealed record UpdateUserRequest(string DisplayName);

public sealed record CreatedUserResponse(Guid Id);
