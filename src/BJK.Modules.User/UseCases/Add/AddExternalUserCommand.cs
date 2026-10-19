using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.SignUp;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Add;

/// <summary>
/// Creates the account record for a user who signed up with Google, Apple or Facebook. The identity server has
/// already asked their date of birth (refusing under 13 before creating anything), created their sign-in account
/// (with the provider's verified email) and linked the provider. Not exposed as an API endpoint; the identity
/// server's sign-in pages send it.
/// </summary>
/// <param name="IdentityId">The new sign-in account's id.</param>
/// <param name="DateOfBirth">The declared date of birth: 13 or over.</param>
public sealed record AddExternalUserCommand(string IdentityId, DateOnly? DateOfBirth) : IRequest<Guid>
{
    /// <summary>Never prints the date of birth.</summary>
    public override string ToString() => $"{nameof(AddExternalUserCommand)}({IdentityId})";
}

public sealed class AddExternalUserCommandValidator : AbstractValidator<AddExternalUserCommand>
{
    public AddExternalUserCommandValidator(TimeProvider clock)
    {
        RuleFor(c => c.IdentityId).NotEmpty();
        RuleFor(c => c.DateOfBirth).ValidDateOfBirth(clock);
    }
}

public sealed class AddExternalUserCommandHandler(IUserRepository users, TimeProvider clock)
    : IRequestHandler<AddExternalUserCommand, Guid>
{
    public async Task<Guid> Handle(AddExternalUserCommand request, CancellationToken cancellationToken)
    {
        var dateOfBirth = request.DateOfBirth
            ?? throw new InvalidOperationException("The validator requires a date of birth.");
        var account = UserAccount.Create(request.IdentityId, dateOfBirth, clock.GetUtcNow());
        if (await users.GetByIdAsync(account.Id, cancellationToken) is null)
        {
            users.Add(account);
            await users.SaveChangesAsync(cancellationToken);
        }

        return account.Id;
    }
}
