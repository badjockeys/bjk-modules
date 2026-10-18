using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Update;

/// <summary>Updates a user's profile. Email changes will go through the identity server, not here.</summary>
public sealed record UpdateUserCommand(Guid UserId, string DisplayName) : IRequest;

public sealed class UpdateUserCommandValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.DisplayName)
            .NotEmpty()
            .MaximumLength(UserProfile.DisplayNameMaxLength);
    }
}

public sealed class UpdateUserCommandHandler(IUserRepository users, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<UpdateUserCommand>
{
    public async Task Handle(UpdateUserCommand request, CancellationToken cancellationToken)
    {
        var user = await UserAccess.GetAccessibleAsync(users, caller, request.UserId, cancellationToken);

        user.UpdateProfile(request.DisplayName, clock.GetUtcNow());
        await users.SaveChangesAsync(cancellationToken);
    }
}
