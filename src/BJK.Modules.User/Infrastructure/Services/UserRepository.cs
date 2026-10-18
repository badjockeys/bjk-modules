using BJK.Modules.Shared.Messaging;
using BJK.Modules.Shared.Users;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.User.Infrastructure.Services;

internal sealed class UserRepository(UserDbContext db) : IUserRepository
{
    public Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Accounts.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<UserAccount> Items, int TotalCount)> ListAsync(
        UserAccountStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Accounts.AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(u => u.Status == s);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyList<UserAccount>> ListDueForDeletionAsync(
        DateTimeOffset now,
        int max,
        CancellationToken cancellationToken) =>
        await db.Accounts
            .Where(u => u.Status == UserAccountStatus.Deactivated && u.DeletionDueAt <= now)
            .OrderBy(u => u.DeletionDueAt)
            .Take(max)
            .ToListAsync(cancellationToken);

    public void Add(UserAccount account) => db.Accounts.Add(account);

    public void Remove(UserAccount account) => db.Accounts.Remove(account);

    public void AddEvent(UserLifecycleEvent userEvent) =>
        db.Outbox.Add(OutboxMessage.For(userEvent.EventId, userEvent, userEvent.OccurredAt));

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public void DiscardChanges() => db.ChangeTracker.Clear();
}
