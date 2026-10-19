using BJK.Modules.User.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BJK.Modules.User.Infrastructure.Data.Configurations;

internal sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.ToTable("Accounts");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(u => u.RowVersion).IsRowVersion();

        // Age check: outcome only. The selfie video and ID photo are never stored.
        builder.Property(u => u.AgeCheckStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(u => u.AgeCheckMethod).HasConversion<string>().HasMaxLength(20);
        builder.Property(u => u.AgeCheckResult).HasConversion<string>().HasMaxLength(30);
        builder.Property(u => u.AgeCheckProvider).HasMaxLength(UserAccount.ProviderMaxLength);

        // Sign-up: the declared date of birth (a date, no time) and the liveness result. Never the video.
        builder.Property(u => u.DateOfBirth).HasColumnType("date");
        builder.Property(u => u.LivenessStatus).HasConversion<string>().HasMaxLength(20);

        // Admin lists, newest first; and the retention job's query.
        builder.HasIndex(u => new { u.Status, u.CreatedAt });
        builder.HasIndex(u => new { u.Status, u.DeletionDueAt });
    }
}
