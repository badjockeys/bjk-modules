using BJK.Modules.Shared.Users;
using BJK.Modules.User.Domain;

namespace BJK.Modules.User.Infrastructure.Services;

public interface IUserRepository
{
    Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Accounts newest first, optionally only those in one status.</summary>
    Task<(IReadOnlyList<UserAccount> Items, int TotalCount)> ListAsync(
        UserAccountStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Deactivated accounts whose grace period has ended, oldest first.</summary>
    Task<IReadOnlyList<UserAccount>> ListDueForDeletionAsync(DateTimeOffset now, int max, CancellationToken cancellationToken);

    void Add(UserAccount account);

    void Remove(UserAccount account);

    /// <summary>Queues an event in the outbox. It's saved with the next <see cref="SaveChangesAsync"/> and then published.</summary>
    void AddEvent(UserLifecycleEvent userEvent);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Forgets unsaved changes, after a save failed, so the next unit of work starts clean.</summary>
    void DiscardChanges();
}
