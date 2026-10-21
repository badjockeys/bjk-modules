using BJK.Modules.User.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace BJK.Modules.User.Tests.Data;

public sealed class UserMigrationsTests
{
    [Fact]
    public void Migrations_never_drop_or_delete_data()
    {
        using var db = new UserDbContextDesignTimeFactory().CreateDbContext([]);
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();

        Assert.NotEmpty(migrationsAssembly.Migrations);

        var destructive = migrationsAssembly.Migrations
            .Select(m => migrationsAssembly.CreateMigration(m.Value, db.Database.ProviderName!))
            .SelectMany(m => m.UpOperations)
            .Where(op => op is DropTableOperation or DropColumnOperation or DropSchemaOperation or DeleteDataOperation)
            .ToList();

        Assert.Empty(destructive);
    }

    [Fact]
    public void Tables_live_in_the_users_schema()
    {
        using var db = new UserDbContextDesignTimeFactory().CreateDbContext([]);

        var entity = db.Model.FindEntityType(typeof(BJK.Modules.User.Domain.UserAccount))!;

        Assert.Equal(UserModule.Schema, entity.GetSchema());
        Assert.Equal("Accounts", entity.GetTableName());
        Assert.Equal(UserModule.Schema, db.Model.FindEntityType(typeof(BJK.Modules.Queues.OutboxMessage))!.GetSchema());
    }
}
