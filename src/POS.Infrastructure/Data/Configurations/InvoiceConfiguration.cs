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
        // A concurrency token (not just an app-level version number) so two truly concurrent writers
        // to the same invoice (e.g. a lost-ack retry racing the original in-flight push) cannot both
        // successfully apply their update from the same stale read — EF adds "WHERE SyncVersion =
        // @original" to the generated UPDATE, and the loser gets a DbUpdateConcurrencyException instead
        // of silently succeeding and double-recording the invoice's stock/financial effect. Mirrors the
        // same fix already applied to CashSession.SyncVersion (see CashSessionConfiguration).
        builder.Property(e => e.SyncVersion).HasDefaultValue(1).IsConcurrencyToken();
        builder.Property(e => e.IsSynced).HasDefaultValue(false);
        builder.Property(e => e.Status).HasConversion<int>();
        builder.HasIndex(e => new { e.StoreId, e.Status });
        builder.HasIndex(e => new { e.StoreId, e.IsSynced });
        builder.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Store)
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Device)
            .WithMany(d => d.Invoices)
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Register)
            .WithMany(r => r.Invoices)
            .HasForeignKey(e => e.RegisterId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.User)
            .WithMany(u => u.Invoices)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
