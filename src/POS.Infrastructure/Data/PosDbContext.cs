using Microsoft.EntityFrameworkCore;
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

    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SyncChange> SyncChanges => Set<SyncChange>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<Payment> Payments => Set<Payment>();

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
                    pending.Add(new PendingSyncChange(auditLog.StoreId, SyncAggregateTypes.AuditLog, auditLog.Id, null));
                    break;

                case Category category:
                    pending.Add(new PendingSyncChange(null, SyncAggregateTypes.Category, category.Id, null));
                    break;

                case Currency:
                    pending.Add(new PendingSyncChange(null, SyncAggregateTypes.CurrencyPolicy, null, null));
                    break;

                case Device device:
                    pending.Add(new PendingSyncChange(device.StoreId, SyncAggregateTypes.Device, device.Id, null));
                    break;

                case Invoice invoice:
                    pending.Add(new PendingSyncChange(invoice.StoreId, SyncAggregateTypes.Invoice, invoice.Id, null));
                    break;

                case Product product:
                    pending.Add(new PendingSyncChange(null, SyncAggregateTypes.Product, product.Id, null));
                    break;

                case User user:
                    pending.Add(new PendingSyncChange(user.StoreId, SyncAggregateTypes.User, user.Id, null));
                    break;

                case Setting setting when !setting.Key.StartsWith("Sync.", StringComparison.OrdinalIgnoreCase):
                    pending.Add(new PendingSyncChange(setting.StoreId, SyncAggregateTypes.Setting, null, setting.Key));
                    break;

                case Store store when entry.State == EntityState.Added || entry.Property(nameof(Store.BaseCurrencyId)).IsModified:
                    pending.Add(new PendingSyncChange(store.Id, SyncAggregateTypes.CurrencyPolicy, store.Id, null));
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
        Guid? StoreId,
        string AggregateType,
        Guid? EntityId,
        string? EntityKey);
}
