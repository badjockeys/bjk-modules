using BJK.Modules.Profiles.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace BJK.Modules.Profiles.Tests.Data;

public sealed class ProfilesMigrationsTests
{
    [Fact]
    public void Migrations_never_drop_or_delete_data()
    {
        using var db = new ProfilesDbContextDesignTimeFactory().CreateDbContext([]);
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
    public void Profiles_table_lives_in_the_profiles_schema_with_unique_handles()
    {
        using var db = new ProfilesDbContextDesignTimeFactory().CreateDbContext([]);

        var entity = db.Model.FindEntityType(typeof(BJK.Modules.Profiles.Domain.Profile))!;

        Assert.Equal(ProfilesModule.Schema, entity.GetSchema());
        Assert.Equal("Profiles", entity.GetTableName());
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique && i.Properties.Single().Name == "Handle");
    }
}
