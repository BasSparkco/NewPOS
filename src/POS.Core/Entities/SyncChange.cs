namespace POS.Core.Entities;

public class SyncChange
{
    public long Id { get; set; }
    public Guid? StoreId { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? EntityKey { get; set; }
    public DateTime ChangedAt { get; set; }

    public Store? Store { get; set; }
}