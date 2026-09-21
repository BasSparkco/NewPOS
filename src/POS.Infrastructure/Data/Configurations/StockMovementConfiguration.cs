using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("StockMovements");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.QuantityDelta).HasPrecision(18, 4);
        builder.Property(e => e.QuantityAfter).HasPrecision(18, 4);
        builder.Property(e => e.Reference).HasMaxLength(100);
        builder.Property(e => e.Notes).HasMaxLength(500);

        builder.HasIndex(e => new { e.StoreId, e.ProductId, e.CreatedAt });
        builder.HasIndex(e => e.InvoiceId);
        builder.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Product)
            .WithMany()
            .HasForeignKey(e => e.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Store)
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Inventory)
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Invoice)
            .WithMany()
            .HasForeignKey(e => e.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.InvoiceItem)
            .WithMany()
            .HasForeignKey(e => e.InvoiceItemId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}