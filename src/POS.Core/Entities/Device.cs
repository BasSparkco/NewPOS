namespace POS.Core.Entities;

public class Device
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int SyncVersion { get; set; }
    public bool IsDeleted { get; set; }

    public Store? Store { get; set; }
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}