using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BJK.Modules.User.Infrastructure.Data;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context without a host. Generating migrations
/// doesn't connect to SQL Server, so the connection string is a placeholder.
/// </summary>
/// <example>
/// <code>
/// dotnet ef migrations add AddSomething --project src/BJK.Modules.User --context UserDbContext --output-dir Infrastructure/Data/Migrations
/// </code>
/// </example>
internal sealed class UserDbContextDesignTimeFactory : IDesignTimeDbContextFactory<UserDbContext>
{
    public UserDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseSqlServer(
                "Server=(design-time);Database=BJK_Design",
                sql => sql.MigrationsHistoryTable(UserModule.MigrationsHistoryTable, UserModule.Schema))
            .Options;

        return new UserDbContext(options);
    }
}
