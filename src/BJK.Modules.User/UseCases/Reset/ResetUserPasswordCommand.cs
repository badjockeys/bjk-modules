using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Reset;

/// <summary>Triggers a password reset email via the identity server, which owns credentials.</summary>
public sealed record ResetUserPasswordCommand(Guid UserId, string CallerIdentityId) : IRequest;

public sealed class ResetUserPasswordCommandValidator : AbstractValidator<ResetUserPasswordCommand>
{
    public ResetUserPasswordCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.CallerIdentityId).NotEmpty();
    }
}

public sealed class ResetUserPasswordCommandHandler(IUserRepository users, IIdentityService identity)
    : IRequestHandler<ResetUserPasswordCommand>
{
    public async Task Handle(ResetUserPasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await UserAccess.GetOwnedAsync(users, request.UserId, request.CallerIdentityId, cancellationToken);

        await identity.SendPasswordResetAsync(user.IdentityId, cancellationToken);
    }
}
