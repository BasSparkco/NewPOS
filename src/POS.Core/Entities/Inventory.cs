namespace POS.Core.Entities;

public class Inventory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid StoreId { get; set; }
    public decimal Quantity { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public Product? Product { get; set; }
    public Store? Store { get; set; }
}
