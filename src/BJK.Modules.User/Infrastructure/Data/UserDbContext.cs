using BJK.Modules.User.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.User.Infrastructure.Data;

/// <summary>
/// The User module's own DbContext. It shares the app's Azure SQL database with other modules
/// but keeps its tables (and migrations history) in the <see cref="UserModule.Schema"/> schema.
/// </summary>
public sealed class UserDbContext(DbContextOptions<UserDbContext> options) : DbContext(options)
{
    public DbSet<UserProfile> Users => Set<UserProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(UserModule.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UserDbContext).Assembly);
    }
}
