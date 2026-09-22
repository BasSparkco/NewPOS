using POS.Core.Enums;

namespace POS.Core.Entities;

/// <summary>
/// One ledger entry within a <see cref="CashSession"/> — an opening float, a sale receipt, a refund
/// payout, or a manual cash addition/removal. Sale/refund rows preserve their link back to the
/// originating <see cref="Invoice"/>/<see cref="Payment"/> without duplicating that data.
/// </summary>
public class CashMovement
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CashSessionId { get; set; }
    public CashMovementType Type { get; set; }
    /// <summary>Always a positive magnitude; <see cref="Type"/> determines the direction.</summary>
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    /// <summary>Set for SaleReceipt/Refund rows — preserves the link to the originating sale.</summary>
    public Guid? InvoiceId { get; set; }
    public Guid? PaymentId { get; set; }
    /// <summary>The cashier who rang up the sale or performed the manual cash movement.</summary>
    public Guid PerformedByUserId { get; set; }
    /// <summary>The manager who authorized a manual CashOut/override, when store policy requires one. Distinct from PerformedByUserId so approval never collapses into self-approval.</summary>
    public Guid? ApprovedByUserId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public CashSession? CashSession { get; set; }
    public Invoice? Invoice { get; set; }
    public Payment? Payment { get; set; }
    public User? PerformedByUser { get; set; }
    public User? ApprovedByUser { get; set; }
}
