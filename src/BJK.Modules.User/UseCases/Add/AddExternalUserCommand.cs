using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Add;

/// <summary>
/// Creates the account record for a user who signed up with Google, Apple or Facebook. The identity server has
/// already created their sign-in account (with the provider's verified email) and linked the provider. Not exposed
/// as an API endpoint; the identity server's sign-in pages send it.
/// </summary>
public sealed record AddExternalUserCommand(string IdentityId) : IRequest<Guid>;

public sealed class AddExternalUserCommandValidator : AbstractValidator<AddExternalUserCommand>
{
    public AddExternalUserCommandValidator()
    {
        RuleFor(c => c.IdentityId).NotEmpty();
    }
}

public sealed class AddExternalUserCommandHandler(IUserRepository users, TimeProvider clock)
    : IRequestHandler<AddExternalUserCommand, Guid>
{
    public async Task<Guid> Handle(AddExternalUserCommand request, CancellationToken cancellationToken)
    {
        var account = UserAccount.Create(request.IdentityId, clock.GetUtcNow());
        if (await users.GetByIdAsync(account.Id, cancellationToken) is null)
        {
            users.Add(account);
            await users.SaveChangesAsync(cancellationToken);
        }

        return account.Id;
    }
}
