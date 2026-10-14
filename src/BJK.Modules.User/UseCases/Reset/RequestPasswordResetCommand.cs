using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Reset;

/// <summary>"Forgot password": sends a reset token to the account's email, if there is an active account.</summary>
public sealed record RequestPasswordResetCommand(string Email) : IRequest;

public sealed class RequestPasswordResetCommandValidator : AbstractValidator<RequestPasswordResetCommand>
{
    public RequestPasswordResetCommandValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress();
    }
}

public sealed class RequestPasswordResetCommandHandler(IIdentityService identity)
    : IRequestHandler<RequestPasswordResetCommand>
{
    public Task Handle(RequestPasswordResetCommand request, CancellationToken cancellationToken) =>
        identity.RequestPasswordResetAsync(request.Email.Trim(), cancellationToken);
}
