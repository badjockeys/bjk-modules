using BJK.Modules.Media.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace BJK.Modules.Media.Tests.Data;

public sealed class MediaMigrationsTests
{
    [Fact]
    public void Migrations_never_drop_or_delete_data()
    {
        using var db = new MediaDbContextDesignTimeFactory().CreateDbContext([]);
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
    public void Media_table_lives_in_the_media_schema()
    {
        using var db = new MediaDbContextDesignTimeFactory().CreateDbContext([]);

        var entity = db.Model.FindEntityType(typeof(BJK.Modules.Media.Domain.MediaItem))!;

        Assert.Equal(MediaModule.Schema, entity.GetSchema());
        Assert.Equal("Media", entity.GetTableName());
    }
}
