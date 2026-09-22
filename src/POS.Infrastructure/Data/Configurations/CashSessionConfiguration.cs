using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Core.Entities;

namespace POS.Infrastructure.Data.Configurations;

internal sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> builder)
    {
        builder.ToTable("CashSessions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.OpeningCashAmount).HasPrecision(18, 2);
        builder.Property(e => e.ClosingCountedAmount).HasPrecision(18, 2);
        builder.Property(e => e.ExpectedCashAmount).HasPrecision(18, 2);
        builder.Property(e => e.DiscrepancyAmount).HasPrecision(18, 2);
        builder.Property(e => e.CurrencyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Status).HasConversion<int>();
        // Doubles as the local optimistic-concurrency guard: every write that touches this session
        // (opening, a manual/sale/refund movement, closing) bumps SyncVersion as part of the same
        // SaveChanges call that writes the actual change. EF adds "WHERE SyncVersion = @original" to
        // the UPDATE, so a payment write and a concurrent CloseSessionAsync can never both silently
        // "win" — whichever commits second gets a DbUpdateConcurrencyException and must reload/retry.
        builder.Property(e => e.SyncVersion).HasDefaultValue(1).IsConcurrencyToken();
        builder.Property(e => e.Notes).HasMaxLength(2000);
        // Exactly one open session per register at a time (SQLite/PostgreSQL both support filtered
        // unique indexes). A shared-mode register still has just one open session; per-cashier mode
        // is enforced in the application service by also checking OpenedByUserId before opening.
        builder.HasIndex(e => e.RegisterId)
            .HasFilter("\"Status\" = 0")
            .IsUnique();
        builder.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Store)
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Register)
            .WithMany(r => r.CashSessions)
            .HasForeignKey(e => e.RegisterId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.OpenedByUser)
            .WithMany()
            .HasForeignKey(e => e.OpenedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.ClosedByUser)
            .WithMany()
            .HasForeignKey(e => e.ClosedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
