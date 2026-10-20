using BJK.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BJK.Modules.Profiles.Infrastructure.Data;

/// <summary>The Profiles module's tables, in the <see cref="ProfilesModule.Schema"/> schema of the app database.</summary>
public sealed class ProfilesDbContext(DbContextOptions<ProfilesDbContext> options) : DbContext(options)
{
    public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<Block> Blocks => Set<Block>();

    public DbSet<Follow> Follows => Set<Follow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(ProfilesModule.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProfilesDbContext).Assembly);
    }
}

internal sealed class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> builder)
    {
        builder.ToTable("Profiles");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Handle).HasMaxLength(Handle.MaxLength).IsRequired();
        builder.HasIndex(p => p.Handle).IsUnique();

        builder.Property(p => p.DisplayName).HasMaxLength(Profile.DisplayNameMaxLength).IsRequired();
        builder.Property(p => p.Bio).HasMaxLength(Profile.BioMaxLength);
        builder.Property(p => p.Location).HasMaxLength(Profile.LocationMaxLength);
        builder.Property(p => p.RowVersion).IsRowVersion();
        builder.Property(p => p.RoadUser).HasConversion<string>().HasMaxLength(20);
        builder.Ignore(p => p.PublicRoadUser);
    }
}

internal sealed class BlockConfiguration : IEntityTypeConfiguration<Block>
{
    public void Configure(EntityTypeBuilder<Block> builder)
    {
        builder.ToTable("Blocks");
        builder.HasKey(b => new { b.BlockerId, b.BlockedId });

        // "Who has blocked this user", for clean-up when an account is deleted.
        builder.HasIndex(b => b.BlockedId);
    }
}

internal sealed class FollowConfiguration : IEntityTypeConfiguration<Follow>
{
    public void Configure(EntityTypeBuilder<Follow> builder)
    {
        builder.ToTable("Follows");
        builder.HasKey(f => new { f.FollowerId, f.FolloweeId });

        // A user's followers, newest first (and their count).
        builder.HasIndex(f => new { f.FolloweeId, f.CreatedAt });
    }
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without a host. No connection is made.</summary>
internal sealed class ProfilesDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ProfilesDbContext>
{
    public ProfilesDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ProfilesDbContext>()
            .UseSqlServer(
                "Server=(design-time);Database=BJK_Design",
                sql => sql.MigrationsHistoryTable(ProfilesModule.MigrationsHistoryTable, ProfilesModule.Schema))
            .Options);
}
