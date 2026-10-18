using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Roles;

/// <summary>
/// Sets an account's staff roles: none, Moderator, or Admin. Admins only. An admin can't take the Admin role away
/// from themselves, so there's always someone who can manage roles.
/// </summary>
public sealed record SetRolesCommand(Guid UserId, IReadOnlyList<string> Roles) : IRequest<UserDto>;

public sealed class SetRolesCommandValidator : AbstractValidator<SetRolesCommand>
{
    public SetRolesCommandValidator()
    {
        RuleFor(c => c.Roles).NotNull();
        RuleForEach(c => c.Roles)
            .Must(r => StaffRoles.All.Contains(r, StringComparer.Ordinal))
            .WithMessage($"Roles are {string.Join(" and ", StaffRoles.All)}.");
    }
}

public sealed class SetRolesCommandHandler(IUserRepository users, IIdentityService identity, ICurrentUser caller)
    : IRequestHandler<SetRolesCommand, UserDto>
{
    public async Task<UserDto> Handle(SetRolesCommand request, CancellationToken cancellationToken)
    {
        caller.RequireAdmin(UserModule.ManageScope);
        var account = await users.GetByIdAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException("User", request.UserId);

        var roles = request.Roles.Distinct(StringComparer.Ordinal).ToList();
        if (caller.UserId() == account.Id && !roles.Contains(StaffRoles.Admin))
        {
            throw new ForbiddenException("You can't remove your own Admin role. Ask another admin.");
        }

        var id = account.Id.ToString("D");
        await identity.SetRolesAsync(id, roles, cancellationToken);
        return UserDto.From(account, await identity.GetAccountAsync(id, cancellationToken));
    }
}
