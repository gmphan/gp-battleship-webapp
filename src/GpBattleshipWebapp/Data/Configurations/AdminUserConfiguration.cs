using GpBattleshipWebapp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GpBattleshipWebapp.Data.Configurations;

public class AdminUserConfiguration : IEntityTypeConfiguration<AdminUser>
{
    public void Configure(EntityTypeBuilder<AdminUser> b)
    {
        b.ToTable("admin_users");

        b.HasKey(x => x.Id);

        // Usernames are stored lower-case; enforce uniqueness there.
        b.Property(x => x.Username)
            .IsRequired()
            .HasMaxLength(64);
        b.HasIndex(x => x.Username).IsUnique();

        b.Property(x => x.DisplayName).HasMaxLength(100);

        b.Property(x => x.PasswordHash).IsRequired().HasMaxLength(256);

        b.Property(x => x.Role)
            .IsRequired()
            .HasMaxLength(32)
            .HasDefaultValue(AdminRoles.Admin);

        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.Property(x => x.CreatedAtUtc).IsRequired().HasColumnType("timestamp with time zone");
        b.Property(x => x.LastLoginUtc).HasColumnType("timestamp with time zone");
    }
}
