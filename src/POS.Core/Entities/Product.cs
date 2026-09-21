namespace POS.Core.Entities;

public class Product
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public decimal Price { get; set; }
    public decimal Cost { get; set; }
    public Guid CategoryId { get; set; }
    public bool IsWeighted { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ImagePath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public Category? Category { get; set; }
    public ICollection<Inventory> Inventories { get; set; } = new List<Inventory>();
    public ICollection<InvoiceItem> InvoiceItems { get; set; } = new List<InvoiceItem>();
}
