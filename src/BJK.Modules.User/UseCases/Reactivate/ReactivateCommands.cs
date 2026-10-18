using BJK.Modules.Shared.Security;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace BJK.Modules.User.UseCases.Reactivate;

/// <summary>
/// A user brings back their deactivated account, within the grace period, with their email and password.
/// Anonymous, because a deactivated account can't sign in. Every failure gives the same answer, so this can't
/// be used to find out which emails are registered or deactivated.
/// </summary>
public sealed record ReactivateAccountCommand(string Email, string Password) : IRequest;

public sealed class ReactivateAccountCommandValidator : AbstractValidator<ReactivateAccountCommand>
{
    public ReactivateAccountCommandValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress();
        RuleFor(c => c.Password).NotEmpty();
    }
}

public sealed class ReactivateAccountCommandHandler(
    IUserRepository users,
    IIdentityService identity,
    AccountLifecycle lifecycle)
    : IRequestHandler<ReactivateAccountCommand>
{
    public async Task Handle(ReactivateAccountCommand request, CancellationToken cancellationToken)
    {
        var identityId = await identity.FindByCredentialsAsync(request.Email.Trim(), request.Password, cancellationToken);
        var account = Guid.TryParse(identityId, out var id) ? await users.GetByIdAsync(id, cancellationToken) : null;

        if (account is null || account.IsActive)
        {
            throw new ValidationException(new[]
            {
                new ValidationFailure(string.Empty, "The email or password is wrong, or the account isn't deactivated."),
            });
        }

        await lifecycle.ReactivateAsync(account, cancellationToken);
    }
}

/// <summary>An admin reactivates someone's deactivated account.</summary>
public sealed record ReactivateUserCommand(Guid UserId) : IRequest;

public sealed class ReactivateUserCommandHandler(IUserRepository users, AccountLifecycle lifecycle, ICurrentUser caller)
    : IRequestHandler<ReactivateUserCommand>
{
    public async Task Handle(ReactivateUserCommand request, CancellationToken cancellationToken)
    {
        UserAccess.RequireAdmin(caller);
        var account = await UserAccess.GetAccessibleAsync(users, caller, request.UserId, cancellationToken);
        await lifecycle.ReactivateAsync(account, cancellationToken);
    }
}
