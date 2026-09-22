using POS.Core.Enums;

namespace POS.Core.Entities;

public class Payment
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public DateTime PaidAt { get; set; }
    /// <summary>
    /// The cash session this payment/refund was recorded against, when one was open on the
    /// register at the time (see <see cref="CashSession"/>). Null for non-cash-drawer flows or when
    /// no session was open — the sale itself is never blocked for lack of an open cash session.
    /// </summary>
    public Guid? CashSessionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Invoice? Invoice { get; set; }
    public CashSession? CashSession { get; set; }
}
