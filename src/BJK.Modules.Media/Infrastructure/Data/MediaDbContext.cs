using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BJK.Modules.Media.Infrastructure.Data;

/// <summary>
/// The Media module's tables, in the <see cref="MediaModule.Schema"/> schema of the app database. When it saves, every
/// media status change other modules should hear about (<see cref="MediaItem.TakeStatusChanges"/>) becomes a
/// <c>media.ready</c> or <c>media.failed</c> message in <c>media.Outbox</c>, in the same transaction, which the API host
/// then publishes to the <see cref="MediaEvents.Topic"/> topic.
/// </summary>
public sealed class MediaDbContext(DbContextOptions<MediaDbContext> options) : DbContext(options)
{
    public DbSet<MediaItem> Media => Set<MediaItem>();

    public DbSet<VideoJob> VideoJobs => Set<VideoJob>();

    /// <summary>Media in posts tagged sensitive (<c>media.SensitiveMedia</c>).</summary>
    public DbSet<SensitiveMediaMark> SensitiveMedia => Set<SensitiveMediaMark>();

    public DbSet<VideoSegment> VideoSegments => Set<VideoSegment>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    /// <summary>Hashes of images that must never be published (<c>media.KnownImageHashes</c>).</summary>
    public DbSet<KnownImageHash> KnownImageHashes => Set<KnownImageHash>();

    /// <summary>Uploads blocked because they matched a known image (<c>media.KnownImageMatches</c>).</summary>
    public DbSet<KnownImageMatch> KnownImageMatches => Set<KnownImageMatch>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AddStatusEventsToOutbox();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AddStatusEventsToOutbox();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(MediaModule.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MediaDbContext).Assembly);
        modelBuilder.AddOutbox();
    }

    private void AddStatusEventsToOutbox()
    {
        foreach (var item in ChangeTracker.Entries<MediaItem>().Select(e => e.Entity).ToList())
        {
            foreach (var change in item.TakeStatusChanges())
            {
                var mediaEvent = new MediaEvent(
                    Guid.CreateVersion7(change.At),
                    item.Id,
                    item.UserId,
                    change.Status == MediaStatus.Ready ? MediaEventType.Ready : MediaEventType.Failed,
                    change.Reason,
                    change.At);
                Outbox.Add(OutboxMessage.For(mediaEvent.EventId, mediaEvent.MessageType, mediaEvent, change.At));
            }
        }
    }
}

internal sealed class MediaItemConfiguration : IEntityTypeConfiguration<MediaItem>
{
    public void Configure(EntityTypeBuilder<MediaItem> builder)
    {
        builder.ToTable("Media");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.MediaType).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);

        builder.Property(m => m.OriginalFileName).HasMaxLength(MediaItem.OriginalFileNameMaxLength).IsRequired();
        builder.Property(m => m.OriginalContentType).HasMaxLength(MediaItem.ContentTypeMaxLength).IsRequired();
        builder.Property(m => m.OriginalKey).HasMaxLength(MediaItem.KeyMaxLength).IsRequired();
        builder.Property(m => m.PlaybackKey).HasMaxLength(MediaItem.KeyMaxLength);
        builder.Property(m => m.ThumbnailKey).HasMaxLength(MediaItem.KeyMaxLength);
        builder.Property(m => m.FeedKey).HasMaxLength(MediaItem.KeyMaxLength);
        builder.Property(m => m.PreviewKey).HasMaxLength(MediaItem.KeyMaxLength);
        builder.Property(m => m.SourceKey).HasMaxLength(MediaItem.KeyMaxLength);
        builder.Property(m => m.DurationSeconds).HasPrecision(9, 3);
        builder.Property(m => m.Sha256).HasMaxLength(64).IsFixedLength();
        builder.Property(m => m.PerceptualHashes).HasMaxLength(MediaItem.PerceptualHashesMaxLength);
        builder.Property(m => m.FailureReason).HasMaxLength(MediaItem.FailureReasonMaxLength);
        builder.Property(m => m.ReviewReason).HasMaxLength(MediaItem.ReviewReasonMaxLength);
        builder.Property(m => m.ManualRedactionsJson);
        builder.Ignore(m => m.ManualRedactions);
        builder.Property(m => m.RowVersion).IsRowVersion();

        // "My uploads", newest first.
        builder.HasIndex(m => new { m.UserId, m.CreatedAt });

        // Clean-up jobs, e.g. uploads abandoned in the Uploading state.
        builder.HasIndex(m => new { m.Status, m.CreatedAt });

        // The unused-uploads sweep: uploads not yet found in use, oldest first.
        builder.HasIndex(m => m.CreatedAt)
            .HasDatabaseName("IX_Media_UnusedUploads")
            .HasFilter("[UsageCheckedAt] IS NULL AND [Status] <> 'Deleted'");
    }
}

internal sealed class SensitiveMediaMarkConfiguration : IEntityTypeConfiguration<SensitiveMediaMark>
{
    public void Configure(EntityTypeBuilder<SensitiveMediaMark> builder)
    {
        builder.ToTable("SensitiveMedia");
        builder.HasKey(m => m.MediaId);
        builder.Property(m => m.MediaId).ValueGeneratedNever();
        builder.Property(m => m.Source).HasConversion<string>().HasMaxLength(20);

        // Removed with the media row (e.g. when an account is permanently deleted).
        builder.HasOne<MediaItem>().WithMany().HasForeignKey(m => m.MediaId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class VideoJobConfiguration : IEntityTypeConfiguration<VideoJob>
{
    public void Configure(EntityTypeBuilder<VideoJob> builder)
    {
        builder.ToTable("VideoJobs");
        builder.HasKey(j => new { j.MediaId, j.Revision });
        builder.Ignore(j => j.Plan);
        builder.Ignore(j => j.SegmentCount);

        // Removed with the media row (e.g. when an account is permanently deleted).
        builder.HasOne<MediaItem>().WithMany().HasForeignKey(j => j.MediaId).OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(j => j.Segments)
            .WithOne()
            .HasForeignKey(s => new { s.MediaId, s.Revision })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(j => j.Segments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class VideoSegmentConfiguration : IEntityTypeConfiguration<VideoSegment>
{
    public void Configure(EntityTypeBuilder<VideoSegment> builder)
    {
        builder.ToTable("VideoSegments");
        builder.HasKey(s => new { s.MediaId, s.Revision, s.Index });
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.ReviewReasons).HasMaxLength(VideoSegment.ReviewReasonsMaxLength);
        builder.Ignore(s => s.IsDone);
        builder.Ignore(s => s.ReviewReasonList);
    }
}

internal sealed class KnownImageHashConfiguration : IEntityTypeConfiguration<KnownImageHash>
{
    public void Configure(EntityTypeBuilder<KnownImageHash> builder)
    {
        builder.ToTable("KnownImageHashes");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.Property(h => h.Kind).HasConversion<string>().HasMaxLength(10);
        builder.Property(h => h.Value).HasMaxLength(64).IsUnicode(false);
        builder.Property(h => h.Source).HasMaxLength(KnownImageHash.SourceMaxLength);
        builder.Property(h => h.Reference).HasMaxLength(KnownImageHash.ReferenceMaxLength);

        // One row per hash per source; syncing a source reads its rows.
        builder.HasIndex(h => new { h.Source, h.Kind, h.Value }).IsUnique();
    }
}

internal sealed class KnownImageMatchConfiguration : IEntityTypeConfiguration<KnownImageMatch>
{
    public void Configure(EntityTypeBuilder<KnownImageMatch> builder)
    {
        builder.ToTable("KnownImageMatches");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Kind).HasConversion<string>().HasMaxLength(10);
        builder.Property(m => m.MatchedValue).HasMaxLength(64).IsUnicode(false);
        builder.Property(m => m.Source).HasMaxLength(KnownImageHash.SourceMaxLength);
        builder.Property(m => m.Reference).HasMaxLength(KnownImageHash.ReferenceMaxLength);
        builder.Property(m => m.Sha256).HasMaxLength(64).IsFixedLength();
        builder.Property(m => m.ReportReference).HasMaxLength(KnownImageHash.ReferenceMaxLength);

        // Kept even if the media row goes (an account deleted): it's the record of what was blocked.
        builder.HasIndex(m => m.MatchedAt);
        builder.HasIndex(m => m.UserId);
        builder.HasIndex(m => m.MediaId);
    }
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without a host. No connection is made.</summary>
internal sealed class MediaDbContextDesignTimeFactory : IDesignTimeDbContextFactory<MediaDbContext>
{
    public MediaDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MediaDbContext>()
            .UseSqlServer(
                "Server=(design-time);Database=BJK_Design",
                sql => sql.MigrationsHistoryTable(MediaModule.MigrationsHistoryTable, MediaModule.Schema))
            .Options);
}
