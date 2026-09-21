using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Username).HasMaxLength(100).IsRequired();
        builder.Property(e => e.NormalizedUsername).HasMaxLength(100).IsRequired();
        builder.Property(e => e.PasswordHash).HasMaxLength(500).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.NormalizedUsername }).IsUnique();
        builder.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Role)
            .WithMany(r => r.Users)
            .HasForeignKey(e => e.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Store)
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
