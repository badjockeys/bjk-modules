using BJK.Modules.Shared.Security;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Confirm;

/// <summary>Sends the signed-in user another email confirmation link.</summary>
public sealed record SendEmailConfirmationCommand : IRequest;

public sealed class SendEmailConfirmationCommandHandler(IUserRepository users, IIdentityService identity, ICurrentUser caller)
    : IRequestHandler<SendEmailConfirmationCommand>
{
    public async Task Handle(SendEmailConfirmationCommand request, CancellationToken cancellationToken)
    {
        var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);
        await identity.SendEmailConfirmationAsync(account.Id.ToString("D"), cancellationToken);
    }
}

/// <summary>
/// Confirms an email address with the token from the confirmation email. Anonymous, because the link may be opened
/// on a device where the user isn't signed in. The new status shows in tokens issued afterwards.
/// </summary>
public sealed record ConfirmEmailCommand(Guid UserId, string Token) : IRequest;

public sealed class ConfirmEmailCommandValidator : AbstractValidator<ConfirmEmailCommand>
{
    public ConfirmEmailCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.Token).NotEmpty();
    }
}

public sealed class ConfirmEmailCommandHandler(IIdentityService identity) : IRequestHandler<ConfirmEmailCommand>
{
    public async Task Handle(ConfirmEmailCommand request, CancellationToken cancellationToken)
    {
        var result = await identity.ConfirmEmailAsync(request.UserId.ToString("D"), request.Token, cancellationToken);
        AccountErrors.ThrowIfFailed(result);
    }
}
