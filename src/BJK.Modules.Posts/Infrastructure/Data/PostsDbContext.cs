using BJK.Modules.Posts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>The Posts module's tables, in the <see cref="PostsModule.Schema"/> schema of the app database.</summary>
public sealed class PostsDbContext(DbContextOptions<PostsDbContext> options) : DbContext(options)
{
    public DbSet<Post> Posts => Set<Post>();

    public DbSet<Reaction> Reactions => Set<Reaction>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<Report> Reports => Set<Report>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(PostsModule.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PostsDbContext).Assembly);
    }
}

internal sealed class PostConfiguration : IEntityTypeConfiguration<Post>
{
    public void Configure(EntityTypeBuilder<Post> builder)
    {
        builder.ToTable("Posts");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Title).HasMaxLength(Post.TitleMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(Post.DescriptionMaxLength);
        builder.Property(p => p.Category).HasConversion<string>().HasMaxLength(40);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Latitude).HasPrecision(9, 6);
        builder.Property(p => p.Longitude).HasPrecision(9, 6);
        builder.Property(p => p.PlaceName).HasMaxLength(Post.PlaceNameMaxLength);
        builder.Property(p => p.HiddenReason).HasMaxLength(Post.HiddenReasonMaxLength);
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.HasMany(p => p.Media)
            .WithOne()
            .HasForeignKey(m => m.PostId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Media).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The feed (newest first), filtered by category, author or area.
        builder.HasIndex(p => new { p.Status, p.CreatedAt });
        builder.HasIndex(p => new { p.Category, p.Status, p.CreatedAt });
        builder.HasIndex(p => new { p.AuthorId, p.CreatedAt });
        builder.HasIndex(p => new { p.Latitude, p.Longitude });
    }
}

internal sealed class PostMediaConfiguration : IEntityTypeConfiguration<PostMedia>
{
    public void Configure(EntityTypeBuilder<PostMedia> builder)
    {
        builder.ToTable("PostMedia");
        builder.HasKey(m => new { m.PostId, m.MediaId });

        // A photo or video belongs to one post only.
        builder.HasIndex(m => m.MediaId).IsUnique();
    }
}

internal sealed class ReactionConfiguration : IEntityTypeConfiguration<Reaction>
{
    public void Configure(EntityTypeBuilder<Reaction> builder)
    {
        builder.ToTable("Reactions");
        builder.HasKey(r => new { r.PostId, r.UserId });
        builder.Property(r => r.Type).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Post>().WithMany().HasForeignKey(r => r.PostId).OnDelete(DeleteBehavior.Cascade);

        // "Posts I reacted to".
        builder.HasIndex(r => new { r.UserId, r.CreatedAt });
    }
}

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.ToTable("Comments");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Body).HasMaxLength(Comment.BodyMaxLength).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.HiddenReason).HasMaxLength(Comment.HiddenReasonMaxLength);
        builder.Property(c => c.RowVersion).IsRowVersion();

        builder.HasOne<Post>().WithMany().HasForeignKey(c => c.PostId).OnDelete(DeleteBehavior.Cascade);

        // A post's comments, oldest first.
        builder.HasIndex(c => new { c.PostId, c.Status, c.CreatedAt });
        builder.HasIndex(c => new { c.AuthorId, c.CreatedAt });
    }
}

internal sealed class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> builder)
    {
        builder.ToTable("Reports");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.TargetType).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Reason).HasConversion<string>().HasMaxLength(40);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Details).HasMaxLength(Report.DetailsMaxLength);
        builder.Property(r => r.ResolutionNote).HasMaxLength(Report.ResolutionNoteMaxLength);

        builder.HasOne<Post>().WithMany().HasForeignKey(r => r.PostId).OnDelete(DeleteBehavior.Cascade);

        // One report per user per post or comment.
        builder.HasIndex(r => new { r.ReporterId, r.TargetType, r.TargetId }).IsUnique();

        // The admin queue, oldest first; and all reports about one item.
        builder.HasIndex(r => new { r.Status, r.CreatedAt });
        builder.HasIndex(r => new { r.TargetType, r.TargetId, r.Status });
    }
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without a host. No connection is made.</summary>
internal sealed class PostsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PostsDbContext>
{
    public PostsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostsDbContext>()
            .UseSqlServer(
                "Server=(design-time);Database=BJK_Design",
                sql => sql.MigrationsHistoryTable(PostsModule.MigrationsHistoryTable, PostsModule.Schema))
            .Options);
}
