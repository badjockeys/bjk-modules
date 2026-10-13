using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Add;

/// <summary>Creates the Bad Jockeys profile for an identity server account. Returns the new user's id.</summary>
public sealed record AddUserCommand(string IdentityId, string Email, string DisplayName) : IRequest<Guid>;

public sealed class AddUserCommandValidator : AbstractValidator<AddUserCommand>
{
    public AddUserCommandValidator(IUserRepository users)
    {
        RuleFor(c => c.IdentityId)
            .NotEmpty()
            .MaximumLength(UserProfile.IdentityIdMaxLength)
            .MustAsync(async (id, ct) => !await users.IdentityExistsAsync(id, ct))
            .WithMessage("This account already has a profile.");

        RuleFor(c => c.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(UserProfile.EmailMaxLength)
            .MustAsync(async (email, ct) => !await users.EmailExistsAsync(email.Trim(), ct))
            .WithMessage("This email address is already in use.");

        RuleFor(c => c.DisplayName)
            .NotEmpty()
            .MaximumLength(UserProfile.DisplayNameMaxLength);
    }
}

public sealed class AddUserCommandHandler(IUserRepository users, TimeProvider clock)
    : IRequestHandler<AddUserCommand, Guid>
{
    public async Task<Guid> Handle(AddUserCommand request, CancellationToken cancellationToken)
    {
        var user = UserProfile.Create(request.IdentityId, request.Email, request.DisplayName, clock.GetUtcNow());

        users.Add(user);
        await users.SaveChangesAsync(cancellationToken);

        return user.Id;
    }
}
