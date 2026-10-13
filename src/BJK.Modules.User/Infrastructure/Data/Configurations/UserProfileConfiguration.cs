using BJK.Modules.User.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BJK.Modules.User.Infrastructure.Data.Configurations;

internal sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.IdentityId)
            .HasMaxLength(UserProfile.IdentityIdMaxLength)
            .IsRequired();
        builder.HasIndex(u => u.IdentityId).IsUnique();

        builder.Property(u => u.Email)
            .HasMaxLength(UserProfile.EmailMaxLength)
            .IsRequired();
        builder.HasIndex(u => u.Email).IsUnique();

        builder.Property(u => u.DisplayName)
            .HasMaxLength(UserProfile.DisplayNameMaxLength)
            .IsRequired();

        builder.Property(u => u.CreatedAt).IsRequired();

        // Soft-deleted users are hidden from every query unless IgnoreQueryFilters() is used.
        builder.HasQueryFilter(u => !u.IsDeleted);
    }
}
