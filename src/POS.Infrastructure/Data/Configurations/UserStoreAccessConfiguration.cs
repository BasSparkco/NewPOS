using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class UserStoreAccessConfiguration : IEntityTypeConfiguration<UserStoreAccess>
{
    public void Configure(EntityTypeBuilder<UserStoreAccess> builder)
    {
        builder.ToTable("UserStoreAccesses");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.TenantId, e.UserId, e.StoreId }).IsUnique();

        builder.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.User)
            .WithMany(u => u.StoreAccesses)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Store)
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
