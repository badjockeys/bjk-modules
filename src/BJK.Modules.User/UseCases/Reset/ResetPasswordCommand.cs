using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Reset;

/// <summary>Sets a new password using the token from <see cref="RequestPasswordResetCommand"/>.</summary>
public sealed record ResetPasswordCommand(string Email, string Token, string NewPassword) : IRequest;

public sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress();
        RuleFor(c => c.Token).NotEmpty();
        RuleFor(c => c.NewPassword).NotEmpty();
    }
}

public sealed class ResetPasswordCommandHandler(IIdentityService identity)
    : IRequestHandler<ResetPasswordCommand>
{
    public async Task Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        var result = await identity.ResetPasswordAsync(
            request.Email.Trim(),
            request.Token,
            request.NewPassword,
            cancellationToken);

        AccountErrors.ThrowIfFailed(result);
    }
}
