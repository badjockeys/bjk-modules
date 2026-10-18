using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Add;

/// <summary>
/// Registers a new user: creates their sign-in account and their account record, and emails a link to confirm their
/// email address (needed before posting or commenting). Returns the new user's id.
/// Their public profile (handle, display name) is created afterwards in bjk-core.
/// </summary>
public sealed record AddUserCommand(string Email, string Password) : IRequest<Guid>;

public sealed class AddUserCommandValidator : AbstractValidator<AddUserCommand>
{
    public const int EmailMaxLength = 256;

    public AddUserCommandValidator()
    {
        // Uniqueness and password strength are checked by the identity server.
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(EmailMaxLength);
        RuleFor(c => c.Password).NotEmpty();
    }
}

public sealed class AddUserCommandHandler(IUserRepository users, IIdentityService identity, TimeProvider clock)
    : IRequestHandler<AddUserCommand, Guid>
{
    public async Task<Guid> Handle(AddUserCommand request, CancellationToken cancellationToken)
    {
        var account = await identity.CreateAccountAsync(request.Email.Trim(), request.Password, cancellationToken);
        AccountErrors.ThrowIfFailed(account);
        var identityId = account.IdentityId
            ?? throw new InvalidOperationException("The identity server didn't return an account id.");

        var user = UserAccount.Create(identityId, clock.GetUtcNow());
        users.Add(user);

        try
        {
            await users.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Don't leave a sign-in account with no account record behind.
            await identity.DeleteAccountAsync(identityId, CancellationToken.None);
            throw;
        }

        await identity.SendEmailConfirmationAsync(identityId, cancellationToken);
        return user.Id;
    }
}
