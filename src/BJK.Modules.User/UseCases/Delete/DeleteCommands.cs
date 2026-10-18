using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.User.UseCases.Delete;

/// <summary>
/// An admin permanently deletes an account straight away, skipping the grace period (for example for a legal
/// request). The sign-in account and account record go now; bjk-core deletes the rest when it gets the event.
/// </summary>
public sealed record DeleteUserNowCommand(Guid UserId) : IRequest;

public sealed class DeleteUserNowCommandHandler(IUserRepository users, AccountLifecycle lifecycle, ICurrentUser caller)
    : IRequestHandler<DeleteUserNowCommand>
{
    public async Task Handle(DeleteUserNowCommand request, CancellationToken cancellationToken)
    {
        UserAccess.RequireAdmin(caller);
        var account = await UserAccess.GetAccessibleAsync(users, caller, request.UserId, cancellationToken);
        await lifecycle.DeletePermanentlyAsync(account, cancellationToken);
    }
}

/// <summary>
/// The retention job: permanently deletes deactivated accounts whose grace period has ended.
/// Run on a timer by <see cref="Infrastructure.AccountRetentionService"/>. Returns how many were deleted.
/// </summary>
public sealed record DeleteExpiredAccountsCommand(int MaxAccounts = 100) : IRequest<int>;

public sealed class DeleteExpiredAccountsCommandHandler(
    IUserRepository users,
    AccountLifecycle lifecycle,
    TimeProvider clock,
    ILogger<DeleteExpiredAccountsCommandHandler> logger)
    : IRequestHandler<DeleteExpiredAccountsCommand, int>
{
    public async Task<int> Handle(DeleteExpiredAccountsCommand request, CancellationToken cancellationToken)
    {
        var due = await users.ListDueForDeletionAsync(clock.GetUtcNow(), request.MaxAccounts, cancellationToken);
        var deleted = 0;

        foreach (UserAccount account in due)
        {
            try
            {
                await lifecycle.DeletePermanentlyAsync(account, cancellationToken);
                deleted++;
                logger.LogInformation("Permanently deleted account {UserId}: its grace period ended at {DueAt}.", account.Id, account.DeletionDueAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Another instance may have got there first; anything else is retried on the next run.
                users.DiscardChanges();
                logger.LogWarning(ex, "Couldn't delete account {UserId}; will retry.", account.Id);
            }
        }

        return deleted;
    }
}
