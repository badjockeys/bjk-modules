using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace BJK.Modules.Posts.Tests.Data;

public sealed class PostsMigrationsTests
{
    [Fact]
    public void Migrations_never_drop_or_delete_data()
    {
        using var db = new PostsDbContextDesignTimeFactory().CreateDbContext([]);
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();

        Assert.NotEmpty(migrationsAssembly.Migrations);

        var destructive = migrationsAssembly.Migrations
            .Select(m => migrationsAssembly.CreateMigration(m.Value, db.Database.ProviderName!))
            .SelectMany(m => m.UpOperations)
            .Where(op => op is DropTableOperation or DropColumnOperation or DropSchemaOperation or DeleteDataOperation)
            .ToList();

        Assert.Empty(destructive);
    }

    [Theory]
    [InlineData(typeof(Post), "Posts")]
    [InlineData(typeof(PostMedia), "PostMedia")]
    [InlineData(typeof(Reaction), "Reactions")]
    [InlineData(typeof(Comment), "Comments")]
    [InlineData(typeof(Report), "Reports")]
    public void Tables_live_in_the_posts_schema(Type entityType, string table)
    {
        using var db = new PostsDbContextDesignTimeFactory().CreateDbContext([]);

        var entity = db.Model.FindEntityType(entityType)!;

        Assert.Equal(PostsModule.Schema, entity.GetSchema());
        Assert.Equal(table, entity.GetTableName());
    }

    [Fact]
    public void A_media_item_can_only_be_in_one_post()
    {
        using var db = new PostsDbContextDesignTimeFactory().CreateDbContext([]);

        var entity = db.Model.FindEntityType(typeof(PostMedia))!;

        Assert.Contains(entity.GetIndexes(), i => i.IsUnique && i.Properties.Single().Name == nameof(PostMedia.MediaId));
    }
}
