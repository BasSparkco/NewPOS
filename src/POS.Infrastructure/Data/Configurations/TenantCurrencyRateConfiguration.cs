using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class TenantCurrencyRateConfiguration : IEntityTypeConfiguration<TenantCurrencyRate>
{
    public void Configure(EntityTypeBuilder<TenantCurrencyRate> builder)
    {
        builder.ToTable("TenantCurrencyRates");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ExchangeRate).HasPrecision(18, 6);
        builder.HasIndex(e => new { e.TenantId, e.CurrencyId }).IsUnique();

        builder.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Currency)
            .WithMany()
            .HasForeignKey(e => e.CurrencyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
