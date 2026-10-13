using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.Delete;

/// <summary>Soft-deletes the caller's profile.</summary>
public sealed record DeleteUserCommand(Guid UserId, string CallerIdentityId) : IRequest;

public sealed class DeleteUserCommandValidator : AbstractValidator<DeleteUserCommand>
{
    public DeleteUserCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.CallerIdentityId).NotEmpty();
    }
}

public sealed class DeleteUserCommandHandler(IUserRepository users, TimeProvider clock)
    : IRequestHandler<DeleteUserCommand>
{
    public async Task Handle(DeleteUserCommand request, CancellationToken cancellationToken)
    {
        var user = await UserAccess.GetOwnedAsync(users, request.UserId, request.CallerIdentityId, cancellationToken);

        user.Delete(clock.GetUtcNow());
        await users.SaveChangesAsync(cancellationToken);
    }
}
