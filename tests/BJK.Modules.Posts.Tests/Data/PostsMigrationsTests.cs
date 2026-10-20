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
    [InlineData(typeof(CommentReaction), "CommentReactions")]
    [InlineData(typeof(Appeal), "Appeals")]
    [InlineData(typeof(PostDraft), "Drafts")]
    [InlineData(typeof(DraftMedia), "DraftMedia")]
    [InlineData(typeof(ModerationAuditRecord), "ModerationAuditLog")]
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

    [Fact]
    public void Public_form_reports_dont_count_against_the_one_report_per_user_rule()
    {
        using var db = new PostsDbContextDesignTimeFactory().CreateDbContext([]);

        var unique = db.Model.FindEntityType(typeof(Report))!.GetIndexes().Single(i => i.IsUnique);

        Assert.Equal("[ReporterId] IS NOT NULL", unique.GetFilter());
    }

    [Fact]
    public void Existing_posts_count_as_having_ready_media()
    {
        using var db = new PostsDbContextDesignTimeFactory().CreateDbContext([]);
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();

        var addMediaState = migrationsAssembly.Migrations
            .Select(m => migrationsAssembly.CreateMigration(m.Value, db.Database.ProviderName!))
            .SelectMany(m => m.UpOperations)
            .OfType<AddColumnOperation>()
            .Single(op => op.Table == "Posts" && op.Name == nameof(Post.MediaState));

        Assert.Equal(nameof(PostMediaState.Ready), addMediaState.DefaultValue);
    }
}
