using BJK.Modules.Shared.Security;
using BJK.Modules.User.Infrastructure.Services;
using MediatR;

namespace BJK.Modules.User.UseCases.Get;

/// <summary>A user's account: your own, or anyone's with the manage scope.</summary>
public sealed record GetUserQuery(Guid UserId) : IRequest<UserDto>;

public sealed class GetUserQueryHandler(IUserRepository users, IIdentityService identity, ICurrentUser caller)
    : IRequestHandler<GetUserQuery, UserDto>
{
    public async Task<UserDto> Handle(GetUserQuery request, CancellationToken cancellationToken)
    {
        var account = await UserAccess.GetAccessibleAsync(users, caller, request.UserId, cancellationToken);
        return UserDto.From(account, await identity.GetAccountAsync(account.Id.ToString("D"), cancellationToken));
    }
}

/// <summary>The signed-in user's own account.</summary>
public sealed record GetCurrentUserQuery : IRequest<UserDto>;

public sealed class GetCurrentUserQueryHandler(IUserRepository users, IIdentityService identity, ICurrentUser caller)
    : IRequestHandler<GetCurrentUserQuery, UserDto>
{
    public async Task<UserDto> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);
        return UserDto.From(account, await identity.GetAccountAsync(account.Id.ToString("D"), cancellationToken));
    }
}
