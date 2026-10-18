using BJK.Modules.Shared.Security;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace BJK.Modules.User.UseCases.Deactivate;

/// <summary>
/// The signed-in user deactivates their own account, confirming with their password. They're signed out
/// everywhere, their content is hidden, and everything is deleted after the grace period (90 days by default)
/// unless they reactivate first.
/// </summary>
public sealed record DeactivateMyAccountCommand(string Password) : IRequest;

public sealed class DeactivateMyAccountCommandValidator : AbstractValidator<DeactivateMyAccountCommand>
{
    public DeactivateMyAccountCommandValidator()
    {
        RuleFor(c => c.Password).NotEmpty();
    }
}

public sealed class DeactivateMyAccountCommandHandler(
    IUserRepository users,
    IIdentityService identity,
    AccountLifecycle lifecycle,
    ICurrentUser caller)
    : IRequestHandler<DeactivateMyAccountCommand>
{
    public async Task Handle(DeactivateMyAccountCommand request, CancellationToken cancellationToken)
    {
        var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);

        if (!await identity.CheckPasswordAsync(account.Id.ToString("D"), request.Password, cancellationToken))
        {
            throw new ValidationException(new[] { new ValidationFailure(nameof(request.Password), "That password isn't right.") });
        }

        await lifecycle.DeactivateAsync(account, cancellationToken);
    }
}

/// <summary>An admin deactivates someone's account. Same effect as the user doing it.</summary>
public sealed record DeactivateUserCommand(Guid UserId) : IRequest;

public sealed class DeactivateUserCommandHandler(IUserRepository users, AccountLifecycle lifecycle, ICurrentUser caller)
    : IRequestHandler<DeactivateUserCommand>
{
    public async Task Handle(DeactivateUserCommand request, CancellationToken cancellationToken)
    {
        UserAccess.RequireAdmin(caller);
        var account = await UserAccess.GetAccessibleAsync(users, caller, request.UserId, cancellationToken);
        await lifecycle.DeactivateAsync(account, cancellationToken);
    }
}
