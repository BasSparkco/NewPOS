using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using POS.Core;
using POS.Core.Entities;

namespace POS.Infrastructure.Data;

public class PosDbContext : DbContext
{
    private bool _writingSyncChanges;

    public PosDbContext(DbContextOptions<PosDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Register> Registers => Set<Register>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<TenantCurrencyRate> TenantCurrencyRates => Set<TenantCurrencyRate>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserStoreAccess> UserStoreAccesses => Set<UserStoreAccess>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SyncChange> SyncChanges => Set<SyncChange>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();

    /// <summary>
    /// Saves changes without recording sync-change rows for them. For one-time structural backfills
    /// of historical data (see <see cref="RegisterBackfill"/>) that must not be replayed through the
    /// ordinary sync engine as if they were fresh business edits.
    /// </summary>
    public int SaveChangesWithoutSyncCapture()
    {
        _writingSyncChanges = true;
        try
        {
            return base.SaveChanges(true);
        }
        finally
        {
            _writingSyncChanges = false;
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (_writingSyncChanges)
            return base.SaveChanges(acceptAllChangesOnSuccess);

        var pendingSyncChanges = CapturePendingSyncChanges();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        PersistSyncChanges(pendingSyncChanges, acceptAllChangesOnSuccess);
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (_writingSyncChanges)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        var pendingSyncChanges = CapturePendingSyncChanges();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await PersistSyncChangesAsync(pendingSyncChanges, acceptAllChangesOnSuccess, cancellationToken);
        return result;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PosDbContext).Assembly);
    }

    /// <summary>
    /// Every DateTime in this app is UTC by convention (DateTime.UtcNow throughout) — but SQLite has no
    /// concept of DateTimeKind and always hands back Kind=Unspecified, which is exactly what
    /// PostgreSQL/Npgsql rejects for a "timestamp with time zone" column ("Cannot write DateTime with
    /// Kind=Unspecified... only UTC is supported"). Found live during the staging WPF rehearsal: a real
    /// SQLite-backed client's own DateTime values (device/register/invoice sync payloads), pushed to
    /// the Postgres-backed API, crashed every push with an unhandled 500 — every earlier live-rehearsal
    /// check had used hand-built JSON with explicit "Z"-suffixed timestamps, which masked this. Forcing
    /// Kind=Utc on every DateTime property, both on write and on read, fixes it at the source for every
    /// provider (a no-op for SQLite, which ignores Kind entirely) rather than patching each call site.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion(typeof(UtcDateTimeConverter));
        configurationBuilder.Properties<DateTime?>().HaveConversion(typeof(UtcNullableDateTimeConverter));
    }

    private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter() : base(
            v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
        {
        }
    }

    private sealed class UtcNullableDateTimeConverter : ValueConverter<DateTime?, DateTime?>
    {
        public UtcNullableDateTimeConverter() : base(
            v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v)
        {
        }
    }

    private IReadOnlyCollection<PendingSyncChange> CapturePendingSyncChanges()
    {
        var pending = new HashSet<PendingSyncChange>();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not EntityState.Added and not EntityState.Modified)
                continue;

            switch (entry.Entity)
            {
                case AuditLog auditLog:
                    pending.Add(new PendingSyncChange(auditLog.TenantId, auditLog.StoreId, SyncAggregateTypes.AuditLog, auditLog.Id, null));
                    break;

                // Movements never trigger their own sync-change row — like InvoiceItem/Payment under
                // Invoice, a CashMovement is nested under its CashSession aggregate. Every write path
                // that adds a movement (open/close/manual/sale/refund) must also touch the parent
                // CashSession itself (bumping UpdatedAt/SyncVersion) — which is exactly what the
                // optimistic-concurrency guard against a racing close already requires, so this falls
                // out "for free" alongside that mechanism rather than needing separate wiring here.
                case CashSession cashSession:
                    pending.Add(new PendingSyncChange(cashSession.TenantId, cashSession.StoreId, SyncAggregateTypes.CashSession, cashSession.Id, null));
                    break;

                case Register register:
                    pending.Add(new PendingSyncChange(register.TenantId, register.StoreId, SyncAggregateTypes.Register, register.Id, null));
                    break;

                case Category category:
                    pending.Add(new PendingSyncChange(category.TenantId, null, SyncAggregateTypes.Category, category.Id, null));
                    break;

                case TenantCurrencyRate rate:
                    pending.Add(new PendingSyncChange(rate.TenantId, null, SyncAggregateTypes.CurrencyPolicy, null, null));
                    break;

                case Device device:
                    pending.Add(new PendingSyncChange(device.TenantId, device.StoreId, SyncAggregateTypes.Device, device.Id, null));
                    break;

                case Invoice invoice:
                    pending.Add(new PendingSyncChange(invoice.TenantId, invoice.StoreId, SyncAggregateTypes.Invoice, invoice.Id, null));
                    break;

                case Product product:
                    pending.Add(new PendingSyncChange(product.TenantId, null, SyncAggregateTypes.Product, product.Id, null));
                    break;

                case User user:
                    pending.Add(new PendingSyncChange(user.TenantId, user.StoreId, SyncAggregateTypes.User, user.Id, null));
                    break;

                case Setting setting when !setting.Key.StartsWith("Sync.", StringComparison.OrdinalIgnoreCase):
                    pending.Add(new PendingSyncChange(setting.TenantId, setting.StoreId, SyncAggregateTypes.Setting, null, setting.Key));
                    break;

                case Store store when entry.State == EntityState.Added || entry.Property(nameof(Store.BaseCurrencyId)).IsModified:
                    pending.Add(new PendingSyncChange(store.TenantId, store.Id, SyncAggregateTypes.CurrencyPolicy, store.Id, null));
                    break;
            }
        }

        return pending;
    }

    private void PersistSyncChanges(IReadOnlyCollection<PendingSyncChange> pendingSyncChanges, bool acceptAllChangesOnSuccess)
    {
        if (pendingSyncChanges.Count == 0)
            return;

        _writingSyncChanges = true;
        try
        {
            var changedAt = DateTime.UtcNow;
            SyncChanges.AddRange(pendingSyncChanges.Select(change => new SyncChange
            {
                TenantId = change.TenantId,
                StoreId = change.StoreId,
                AggregateType = change.AggregateType,
                EntityId = change.EntityId,
                EntityKey = change.EntityKey,
                ChangedAt = changedAt
            }));
            base.SaveChanges(acceptAllChangesOnSuccess);
        }
        finally
        {
            _writingSyncChanges = false;
        }
    }

    private async Task PersistSyncChangesAsync(
        IReadOnlyCollection<PendingSyncChange> pendingSyncChanges,
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        if (pendingSyncChanges.Count == 0)
            return;

        _writingSyncChanges = true;
        try
        {
            var changedAt = DateTime.UtcNow;
            SyncChanges.AddRange(pendingSyncChanges.Select(change => new SyncChange
            {
                TenantId = change.TenantId,
                StoreId = change.StoreId,
                AggregateType = change.AggregateType,
                EntityId = change.EntityId,
                EntityKey = change.EntityKey,
                ChangedAt = changedAt
            }));
            await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        finally
        {
            _writingSyncChanges = false;
        }
    }

    private sealed record PendingSyncChange(
        Guid TenantId,
        Guid? StoreId,
        string AggregateType,
        Guid? EntityId,
        string? EntityKey);
}
