using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.SignUp;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Add;

/// <summary>
/// Registers a new user: creates their sign-in account and their account record (with their date of birth), and
/// emails a link to confirm their email address (needed before posting or commenting). Returns the new user's id.
/// Under 13 is refused before anything is created, and that date of birth isn't kept anywhere. The new account
/// can't get tokens until it passes the liveness check on the identity server's sign-up pages. Their public profile
/// (handle, display name) is created afterwards in bjk-core.
/// </summary>
/// <param name="Email">The email address to sign in with.</param>
/// <param name="Password">The password.</param>
/// <param name="DateOfBirth">The declared date of birth: 13 or over.</param>
public sealed record AddUserCommand(string Email, string Password, DateOnly? DateOfBirth) : IRequest<Guid>
{
    /// <summary>Never prints the password or the date of birth.</summary>
    public override string ToString() => $"{nameof(AddUserCommand)}({Email})";
}

public sealed class AddUserCommandValidator : AbstractValidator<AddUserCommand>
{
    public const int EmailMaxLength = 256;

    public AddUserCommandValidator(TimeProvider clock)
    {
        // Uniqueness and password strength are checked by the identity server.
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(EmailMaxLength);
        RuleFor(c => c.Password).NotEmpty();
        RuleFor(c => c.DateOfBirth).ValidDateOfBirth(clock);
    }
}

public sealed class AddUserCommandHandler(IUserRepository users, IIdentityService identity, TimeProvider clock)
    : IRequestHandler<AddUserCommand, Guid>
{
    public async Task<Guid> Handle(AddUserCommand request, CancellationToken cancellationToken)
    {
        var dateOfBirth = request.DateOfBirth
            ?? throw new InvalidOperationException("The validator requires a date of birth.");

        var account = await identity.CreateAccountAsync(request.Email.Trim(), request.Password, cancellationToken);
        AccountErrors.ThrowIfFailed(account);
        var identityId = account.IdentityId
            ?? throw new InvalidOperationException("The identity server didn't return an account id.");

        var user = UserAccount.Create(identityId, dateOfBirth, clock.GetUtcNow());
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
