namespace POS.Core.Entities;

public class SyncChange
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>Null for tenant-wide aggregates (e.g. shared catalog) not tied to one branch.</summary>
    public Guid? StoreId { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? EntityKey { get; set; }
    public DateTime ChangedAt { get; set; }

    public Tenant? Tenant { get; set; }
    public Store? Store { get; set; }
}