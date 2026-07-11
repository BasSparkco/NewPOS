using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.TotalAmount).HasPrecision(18, 2);
        builder.Property(e => e.TaxPercent).HasPrecision(5, 2);
        builder.Property(e => e.Currency).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Notes).HasMaxLength(2000);
        builder.Property(e => e.SyncVersion).HasDefaultValue(1);
        builder.Property(e => e.IsSynced).HasDefaultValue(false);
        builder.Property(e => e.Status).HasConversion<int>();
        builder.HasIndex(e => new { e.StoreId, e.Status });
        builder.HasIndex(e => new { e.StoreId, e.IsSynced });
        builder.HasOne(e => e.Store)
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Device)
            .WithMany(d => d.Invoices)
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.User)
            .WithMany(u => u.Invoices)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
