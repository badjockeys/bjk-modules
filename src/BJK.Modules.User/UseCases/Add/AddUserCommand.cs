using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Add;

/// <summary>
/// Registers a new user: creates their sign-in account in the identity server and their
/// Bad Jockeys profile. Returns the new user's id.
/// </summary>
public sealed record AddUserCommand(string Email, string Password, string DisplayName) : IRequest<Guid>;

public sealed class AddUserCommandValidator : AbstractValidator<AddUserCommand>
{
    public AddUserCommandValidator(IUserRepository users)
    {
        RuleFor(c => c.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(UserProfile.EmailMaxLength)
            .MustAsync(async (email, ct) => !await users.EmailExistsAsync(email.Trim(), ct))
            .WithMessage("This email address is already in use.");

        // Strength rules are enforced by the identity server's password policy.
        RuleFor(c => c.Password).NotEmpty();

        RuleFor(c => c.DisplayName)
            .NotEmpty()
            .MaximumLength(UserProfile.DisplayNameMaxLength);
    }
}

public sealed class AddUserCommandHandler(IUserRepository users, IIdentityService identity, TimeProvider clock)
    : IRequestHandler<AddUserCommand, Guid>
{
    public async Task<Guid> Handle(AddUserCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();

        var account = await identity.CreateAccountAsync(email, request.Password, cancellationToken);
        AccountErrors.ThrowIfFailed(account);
        var identityId = account.IdentityId
            ?? throw new InvalidOperationException("The identity server didn't return an account id.");

        var user = UserProfile.Create(identityId, email, request.DisplayName, clock.GetUtcNow());
        users.Add(user);

        try
        {
            await users.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Don't leave a sign-in account with no profile behind.
            await identity.DeleteAccountAsync(identityId, CancellationToken.None);
            throw;
        }

        return user.Id;
    }
}
