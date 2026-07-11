using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class SyncChangeConfiguration : IEntityTypeConfiguration<SyncChange>
{
    public void Configure(EntityTypeBuilder<SyncChange> builder)
    {
        builder.ToTable("SyncChanges");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.Property(e => e.AggregateType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.EntityKey).HasMaxLength(200);

        builder.HasIndex(e => new { e.AggregateType, e.Id });
        builder.HasIndex(e => new { e.StoreId, e.AggregateType, e.Id });

        builder.HasOne(e => e.Store)
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}