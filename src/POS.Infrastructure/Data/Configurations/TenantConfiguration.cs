using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(500).IsRequired();
        builder.Property(e => e.NormalizedSlug).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Status).HasConversion<int>();
        builder.HasIndex(e => e.NormalizedSlug).IsUnique();
    }
}
