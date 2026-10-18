using BJK.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BJK.Modules.Media.Infrastructure.Data;

/// <summary>The Media module's tables, in the <see cref="MediaModule.Schema"/> schema of the app database.</summary>
public sealed class MediaDbContext(DbContextOptions<MediaDbContext> options) : DbContext(options)
{
    public DbSet<MediaItem> Media => Set<MediaItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(MediaModule.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MediaDbContext).Assembly);
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
        builder.Property(m => m.DurationSeconds).HasPrecision(9, 3);
        builder.Property(m => m.Sha256).HasMaxLength(64).IsFixedLength();
        builder.Property(m => m.FailureReason).HasMaxLength(MediaItem.FailureReasonMaxLength);
        builder.Property(m => m.ReviewReason).HasMaxLength(MediaItem.ReviewReasonMaxLength);
        builder.Property(m => m.ManualRedactionsJson);
        builder.Ignore(m => m.ManualRedactions);
        builder.Property(m => m.RowVersion).IsRowVersion();

        // "My uploads", newest first.
        builder.HasIndex(m => new { m.UserId, m.CreatedAt });

        // Clean-up jobs, e.g. uploads abandoned in the Uploading state.
        builder.HasIndex(m => new { m.Status, m.CreatedAt });
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
