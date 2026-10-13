using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Update;

/// <summary>Updates the caller's profile. Email changes go through the identity server, not here.</summary>
public sealed record UpdateUserCommand(Guid UserId, string CallerIdentityId, string DisplayName) : IRequest;

public sealed class UpdateUserCommandValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.CallerIdentityId).NotEmpty();
        RuleFor(c => c.DisplayName)
            .NotEmpty()
            .MaximumLength(UserProfile.DisplayNameMaxLength);
    }
}

public sealed class UpdateUserCommandHandler(IUserRepository users, TimeProvider clock)
    : IRequestHandler<UpdateUserCommand>
{
    public async Task Handle(UpdateUserCommand request, CancellationToken cancellationToken)
    {
        var user = await UserAccess.GetOwnedAsync(users, request.UserId, request.CallerIdentityId, cancellationToken);

        user.UpdateProfile(request.DisplayName, clock.GetUtcNow());
        await users.SaveChangesAsync(cancellationToken);
    }
}
