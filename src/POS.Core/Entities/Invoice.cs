using POS.Core.Enums;

namespace POS.Core.Entities;

public class Invoice
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    /// <summary>The physical Box (machine) this sale was rung up on. Required — every invoice is keyed by tenant + branch (StoreId) + box (DeviceId) + user.</summary>
    public Guid DeviceId { get; set; }
    /// <summary>
    /// The logical Register this sale belongs to (stable across a machine swap). Nullable only for
    /// rows created before Registers existed; the startup backfill assigns it from the invoice's
    /// Device at that time, and every new invoice populates it going forward.
    /// </summary>
    public Guid? RegisterId { get; set; }
    public Guid UserId { get; set; }
    public Guid? CustomerId { get; set; }
    public InvoiceStatus Status { get; set; }
    public decimal TotalAmount { get; set; }
    /// <summary>Invoice-level tax rate in % (e.g. 17 for 17% VAT). Applied to the after-discount subtotal.</summary>
    public decimal TaxPercent { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Notes { get; set; }
    public bool IsSynced { get; set; }
    public int SyncVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public Device? Device { get; set; }
    public Register? Register { get; set; }
    public Store? Store { get; set; }
    public User? User { get; set; }
    public ICollection<InvoiceItem> Items { get; set; } = new List<InvoiceItem>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
