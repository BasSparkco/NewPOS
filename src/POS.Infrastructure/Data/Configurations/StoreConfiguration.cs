using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> builder)
    {
        builder.ToTable("Stores");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Address).HasMaxLength(1000);
        builder.Property(e => e.Phone).HasMaxLength(50);
        builder.HasOne(e => e.Tenant)
            .WithMany(t => t.Stores)
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.BaseCurrency)
            .WithMany()
            .HasForeignKey(e => e.BaseCurrencyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
