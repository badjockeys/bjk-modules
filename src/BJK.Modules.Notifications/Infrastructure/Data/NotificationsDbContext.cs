using BJK.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BJK.Modules.Notifications.Infrastructure.Data;

/// <summary>The Notifications module's tables, in the <see cref="NotificationsModule.Schema"/> schema of the app database.</summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationSetting> Settings => Set<NotificationSetting>();

    public DbSet<NotificationContact> Contacts => Set<NotificationContact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(NotificationsModule.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.Type).HasConversion<string>().HasMaxLength(40);
        builder.Property(n => n.PostTitle).HasMaxLength(Notification.PostTitleMaxLength);
        builder.Ignore(n => n.IsRead);

        // The bell: a user's newest first, and their unread count.
        builder.HasIndex(n => new { n.UserId, n.UpdatedAt });
        builder.HasIndex(n => new { n.UserId, n.ReadAt });

        // The clean-up job.
        builder.HasIndex(n => n.CreatedAt);
    }
}

internal sealed class NotificationSettingConfiguration : IEntityTypeConfiguration<NotificationSetting>
{
    public void Configure(EntityTypeBuilder<NotificationSetting> builder)
    {
        builder.ToTable("NotificationSettings");
        builder.HasKey(s => new { s.UserId, s.Type });
        builder.Property(s => s.Type).HasConversion<string>().HasMaxLength(40);
    }
}

internal sealed class NotificationContactConfiguration : IEntityTypeConfiguration<NotificationContact>
{
    public void Configure(EntityTypeBuilder<NotificationContact> builder)
    {
        builder.ToTable("NotificationContacts");
        builder.HasKey(c => c.UserId);
        builder.Property(c => c.UserId).ValueGeneratedNever();
        builder.Property(c => c.Email).HasMaxLength(NotificationContact.EmailMaxLength).IsRequired();
    }
}

internal sealed class NotificationsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseSqlServer(
                "Server=(design-time);Database=BJK_Design",
                sql => sql.MigrationsHistoryTable(NotificationsModule.MigrationsHistoryTable, NotificationsModule.Schema))
            .Options);
}
