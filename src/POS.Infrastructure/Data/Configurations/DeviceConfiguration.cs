using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("Devices");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.SyncVersion).HasDefaultValue(1);
        builder.Property(e => e.EnrollmentCodeHash).HasMaxLength(200);
        builder.HasIndex(e => new { e.StoreId, e.Name }).IsUnique();
        builder.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Store)
            .WithMany(s => s.Devices)
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}