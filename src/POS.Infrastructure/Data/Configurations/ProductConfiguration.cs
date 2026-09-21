using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Barcode).HasMaxLength(100);
        builder.Property(e => e.Price).HasPrecision(18, 2);
        builder.Property(e => e.Cost).HasPrecision(18, 2);
        builder.Property(e => e.ImagePath).HasMaxLength(1000);
        builder.HasIndex(e => e.Barcode);
        builder.HasIndex(e => e.TenantId);
        builder.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(e => e.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
