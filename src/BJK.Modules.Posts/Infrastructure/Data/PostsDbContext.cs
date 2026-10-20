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

    public DbSet<CommentReaction> CommentReactions => Set<CommentReaction>();

    public DbSet<Report> Reports => Set<Report>();

    public DbSet<Appeal> Appeals => Set<Appeal>();

    public DbSet<ModerationAuditRecord> ModerationAuditLog => Set<ModerationAuditRecord>();

    public DbSet<ReelPlay> ReelPlays => Set<ReelPlay>();

    public DbSet<ReelPlayDailyCount> ReelPlayDailyCounts => Set<ReelPlayDailyCount>();

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
        builder.Property(p => p.ScreeningLabels).HasMaxLength(ScreeningText.LabelsMaxLength);
        builder.Property(p => p.MediaState).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.MediaFailureReason).HasMaxLength(Post.MediaFailureReasonMaxLength);
        builder.Property(p => p.NotifyEmail).HasMaxLength(Post.NotifyEmailMaxLength);
        builder.Property(p => p.SensitiveSource).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.SensitiveReview).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.RowVersion).IsRowVersion();
        builder.Ignore(p => p.Types);
        builder.Ignore(p => p.IsMediaPending);
        builder.Ignore(p => p.AuthorMayRemoveSensitive);

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

        // The content screening queues (held, flagged).
        builder.HasIndex(p => new { p.IsFlagged, p.Status, p.CreatedAt });

        // The sensitive-review queue (automatic tags waiting for a moderator).
        builder.HasIndex(p => new { p.SensitiveReview, p.CreatedAt });

        // Reels: public video posts by ranking (all categories, and one category). Filtered to the posts reels can
        // show, so they stay small; the reels query uses the same conditions (see ReelRepository).
        builder.HasIndex(p => new { p.ReelScore, p.CreatedAt })
            .HasDatabaseName("IX_Posts_Reels")
            .HasFilter(PublicVideoFilter);
        builder.HasIndex(p => new { p.Category, p.ReelScore, p.CreatedAt })
            .HasDatabaseName("IX_Posts_Reels_Category")
            .HasFilter(PublicVideoFilter);

        // The heat map: public posts by incident time, covering everything it counts, so a period is one range seek
        // with no lookups (see HeatmapRepository).
        builder.HasIndex(p => p.IncidentAt)
            .HasDatabaseName("IX_Posts_Heatmap")
            .HasFilter(PublicFilter)
            .IncludeProperties(p => new { p.Latitude, p.Longitude, p.Category, p.TypeFlags, p.PlaceName });
    }

    /// <summary>Published, media ready, author active: what everyone can see.</summary>
    internal const string PublicFilter = "[Status] = N'Published' AND [MediaState] = N'Ready' AND [IsAuthorDeactivated] = 0";

    /// <summary><see cref="PublicFilter"/>, video posts only.</summary>
    internal const string PublicVideoFilter = "[HasVideo] = 1 AND " + PublicFilter;
}

internal sealed class ReelPlayConfiguration : IEntityTypeConfiguration<ReelPlay>
{
    public void Configure(EntityTypeBuilder<ReelPlay> builder)
    {
        builder.ToTable("ReelPlays");

        // One play per viewer, day and post: repeats change nothing, and today's count is a range of the key.
        builder.HasKey(p => new { p.ViewerKey, p.Day, p.PostId });

        builder.HasOne<Post>().WithMany().HasForeignKey(p => p.PostId).OnDelete(DeleteBehavior.Cascade);

        // The retention sweep.
        builder.HasIndex(p => new { p.Day, p.IsAnonymous });
    }
}

internal sealed class ReelPlayDailyCountConfiguration : IEntityTypeConfiguration<ReelPlayDailyCount>
{
    public void Configure(EntityTypeBuilder<ReelPlayDailyCount> builder)
    {
        builder.ToTable("ReelPlayDailyCounts");
        builder.HasKey(c => new { c.PostId, c.Day });
        builder.HasOne<Post>().WithMany().HasForeignKey(c => c.PostId).OnDelete(DeleteBehavior.Cascade);
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
        builder.Property(c => c.ScreeningLabels).HasMaxLength(ScreeningText.LabelsMaxLength);
        builder.Property(c => c.RowVersion).IsRowVersion();

        builder.HasOne<Post>().WithMany().HasForeignKey(c => c.PostId).OnDelete(DeleteBehavior.Cascade);

        // A post's comments, oldest first.
        builder.HasIndex(c => new { c.PostId, c.Status, c.CreatedAt });
        builder.HasIndex(c => new { c.AuthorId, c.CreatedAt });

        // The content screening queues (held, flagged).
        builder.HasIndex(c => new { c.IsFlagged, c.Status, c.CreatedAt });

        // A comment's replies, oldest first. No foreign key: a reply stays (under a "comment removed" placeholder)
        // when its top-level comment is deleted, including when that comment's author deletes their account.
        builder.HasIndex(c => new { c.ParentCommentId, c.Status, c.CreatedAt });
    }
}

internal sealed class CommentReactionConfiguration : IEntityTypeConfiguration<CommentReaction>
{
    public void Configure(EntityTypeBuilder<CommentReaction> builder)
    {
        builder.ToTable("CommentReactions");
        builder.HasKey(r => new { r.CommentId, r.UserId });
        builder.Property(r => r.Type).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Comment>().WithMany().HasForeignKey(r => r.CommentId).OnDelete(DeleteBehavior.Cascade);

        // For deleting a user's reactions with their account.
        builder.HasIndex(r => r.UserId);
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
        builder.Property(r => r.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Priority).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Details).HasMaxLength(Report.DetailsMaxLength);
        builder.Property(r => r.ResolutionNote).HasMaxLength(Report.ResolutionNoteMaxLength);
        builder.Property(r => r.ReporterName).HasMaxLength(Report.ReporterNameMaxLength);
        builder.Property(r => r.ContactEmail).HasMaxLength(Report.ContactEmailMaxLength);
        builder.Property(r => r.TargetUrl).HasMaxLength(Report.TargetUrlMaxLength);
        builder.Ignore(r => r.Reference);

        // Profile reports have no post.
        builder.HasOne<Post>().WithMany().HasForeignKey(r => r.PostId).IsRequired(false).OnDelete(DeleteBehavior.Cascade);

        // One report per signed-in user per post, comment or profile. Public-form reports have no reporter id.
        builder.HasIndex(r => new { r.ReporterId, r.TargetType, r.TargetId })
            .IsUnique()
            .HasFilter("[ReporterId] IS NOT NULL");

        // The moderators' queue, urgent first; and all reports about one item.
        builder.HasIndex(r => new { r.Status, r.CreatedAt });
        builder.HasIndex(r => new { r.Status, r.Priority, r.DueAt });
        builder.HasIndex(r => new { r.TargetType, r.TargetId, r.Status });
    }
}

internal sealed class AppealConfiguration : IEntityTypeConfiguration<Appeal>
{
    public void Configure(EntityTypeBuilder<Appeal> builder)
    {
        builder.ToTable("Appeals");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.TargetType).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.Statement).HasMaxLength(Appeal.StatementMaxLength).IsRequired();
        builder.Property(a => a.ContactEmail).HasMaxLength(Report.ContactEmailMaxLength);
        builder.Property(a => a.DecisionNote).HasMaxLength(Appeal.DecisionNoteMaxLength);
        builder.Ignore(a => a.Reference);

        builder.HasOne<Post>().WithMany().HasForeignKey(a => a.PostId).OnDelete(DeleteBehavior.Cascade);

        // One appeal per user per post or comment.
        builder.HasIndex(a => new { a.AppellantId, a.TargetType, a.TargetId }).IsUnique();

        // The moderators' queue, oldest due first.
        builder.HasIndex(a => new { a.Status, a.DueAt });
    }
}

internal sealed class ModerationAuditRecordConfiguration : IEntityTypeConfiguration<ModerationAuditRecord>
{
    public void Configure(EntityTypeBuilder<ModerationAuditRecord> builder)
    {
        builder.ToTable("ModerationAuditLog");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.ActorRole).HasMaxLength(ModerationAuditRecord.ActorRoleMaxLength).IsRequired();
        builder.Property(a => a.Module).HasMaxLength(ModerationAuditRecord.ModuleMaxLength).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(ModerationAuditRecord.ActionMaxLength).IsRequired();
        builder.Property(a => a.TargetType).HasMaxLength(ModerationAuditRecord.TargetTypeMaxLength).IsRequired();
        builder.Property(a => a.TargetId).HasMaxLength(ModerationAuditRecord.TargetIdMaxLength).IsRequired();
        builder.Property(a => a.Reason).HasMaxLength(ModerationAuditRecord.ReasonMaxLength).IsRequired();
        builder.Property(a => a.Before).HasMaxLength(ModerationAuditRecord.SummaryMaxLength);
        builder.Property(a => a.After).HasMaxLength(ModerationAuditRecord.SummaryMaxLength);

        // Newest first, filtered by actor, action or target.
        builder.HasIndex(a => a.OccurredAt);
        builder.HasIndex(a => new { a.ActorId, a.OccurredAt });
        builder.HasIndex(a => new { a.Action, a.OccurredAt });
        builder.HasIndex(a => new { a.TargetType, a.TargetId });
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
