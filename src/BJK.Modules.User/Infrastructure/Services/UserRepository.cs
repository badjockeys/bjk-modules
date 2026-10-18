using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.User.Infrastructure.Services;

internal sealed class UserRepository(UserDbContext db) : IUserRepository
{
    public Task<UserProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<UserProfile?> GetByIdentityIdAsync(string identityId, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.IdentityId == identityId, cancellationToken);

    public async Task<(IReadOnlyList<UserProfile> Items, int TotalCount)> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    // Includes soft-deleted rows, because the unique index does.
    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) =>
        db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, cancellationToken);

    public void Add(UserProfile user) => db.Users.Add(user);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
