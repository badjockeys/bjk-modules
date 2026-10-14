using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Infrastructure.Services;
using MediatR;

namespace BJK.Modules.User.UseCases.Get;

/// <summary>Gets a user's profile: your own, or anyone's with the manage scope.</summary>
public sealed record GetUserQuery(Guid UserId) : IRequest<UserDto>;

public sealed class GetUserQueryHandler(IUserRepository users, ICurrentUser caller)
    : IRequestHandler<GetUserQuery, UserDto>
{
    public async Task<UserDto> Handle(GetUserQuery request, CancellationToken cancellationToken)
    {
        var user = await UserAccess.GetAccessibleAsync(users, caller, request.UserId, cancellationToken);
        return UserDto.From(user);
    }
}

/// <summary>Gets the signed-in user's own profile.</summary>
public sealed record GetCurrentUserQuery : IRequest<UserDto>;

public sealed class GetCurrentUserQueryHandler(IUserRepository users, ICurrentUser caller)
    : IRequestHandler<GetCurrentUserQuery, UserDto>
{
    public async Task<UserDto> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        var identityId = caller.IdentityId
            ?? throw new ForbiddenException("This needs a signed-in user's token.");

        var user = await users.GetByIdentityIdAsync(identityId, cancellationToken)
            ?? throw new NotFoundException("User", identityId);

        return UserDto.From(user);
    }
}
