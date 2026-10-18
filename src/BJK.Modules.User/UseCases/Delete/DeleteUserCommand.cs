using BJK.Modules.Shared.Security;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Delete;

/// <summary>Soft-deletes a user's profile and disables their sign-in account. Nothing is removed from the database.</summary>
public sealed record DeleteUserCommand(Guid UserId) : IRequest;

public sealed class DeleteUserCommandValidator : AbstractValidator<DeleteUserCommand>
{
    public DeleteUserCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
    }
}

public sealed class DeleteUserCommandHandler(
    IUserRepository users,
    IIdentityService identity,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<DeleteUserCommand>
{
    public async Task Handle(DeleteUserCommand request, CancellationToken cancellationToken)
    {
        var user = await UserAccess.GetAccessibleAsync(users, caller, request.UserId, cancellationToken);

        // Disable sign-in first: if saving the profile then fails, the request can safely be retried.
        await identity.DisableAccountAsync(user.IdentityId, cancellationToken);

        user.Delete(clock.GetUtcNow());
        await users.SaveChangesAsync(cancellationToken);
    }
}
