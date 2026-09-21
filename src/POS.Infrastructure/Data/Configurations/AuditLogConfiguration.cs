using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Action).HasMaxLength(100).IsRequired();
        builder.Property(e => e.EntityName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Details).HasMaxLength(4000);

        builder.HasIndex(e => new { e.TenantId, e.CreatedAt });
        builder.HasIndex(e => new { e.StoreId, e.CreatedAt });
        builder.HasIndex(e => new { e.StoreId, e.Action, e.EntityName, e.CreatedAt });

        builder.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Store)
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}