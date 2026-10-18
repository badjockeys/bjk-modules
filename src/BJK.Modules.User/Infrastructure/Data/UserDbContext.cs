using BJK.Modules.Shared.Messaging;
using BJK.Modules.User.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.User.Infrastructure.Data;

/// <summary>
/// The User module's own DbContext, in the <see cref="UserModule.Schema"/> schema of the identity database:
/// account status, and the outbox of user lifecycle events waiting to be published.
/// </summary>
public sealed class UserDbContext(DbContextOptions<UserDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Accounts => Set<UserAccount>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(UserModule.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UserDbContext).Assembly);
        modelBuilder.AddOutbox();
    }
}
