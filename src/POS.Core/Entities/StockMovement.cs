using POS.Core.Enums;

namespace POS.Core.Entities;

public class StockMovement
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid StoreId { get; set; }
    public Guid? InventoryId { get; set; }
    public Guid? InvoiceId { get; set; }
    public Guid? InvoiceItemId { get; set; }
    public Guid? UserId { get; set; }
    public StockMovementType Type { get; set; }
    public decimal QuantityDelta { get; set; }
    public decimal QuantityAfter { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    /// <summary>True when this movement's reconciled result drove Inventory.Quantity negative — a
    /// visible discrepancy for manager review, never a silently clamped/discarded/overwritten value.</summary>
    public bool IsDiscrepancy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public Product? Product { get; set; }
    public Store? Store { get; set; }
    public Inventory? Inventory { get; set; }
    public Invoice? Invoice { get; set; }
    public InvoiceItem? InvoiceItem { get; set; }
    public User? User { get; set; }
}