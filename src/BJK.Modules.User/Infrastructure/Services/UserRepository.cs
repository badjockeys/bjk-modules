using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.User.Infrastructure.Services;

internal sealed class UserRepository(UserDbContext db) : IUserRepository
{
    public Task<UserProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    // Uniqueness checks include soft-deleted rows, because the unique indexes do.
    public Task<bool> IdentityExistsAsync(string identityId, CancellationToken cancellationToken) =>
        db.Users.IgnoreQueryFilters().AnyAsync(u => u.IdentityId == identityId, cancellationToken);

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) =>
        db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, cancellationToken);

    public void Add(UserProfile user) => db.Users.Add(user);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
