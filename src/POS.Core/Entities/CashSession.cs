using POS.Core.Enums;

namespace POS.Core.Entities;

/// <summary>
/// A financial cash-drawer session on one <see cref="Register"/>: the opening float through the
/// closing count, with every cash movement in between recorded as a <see cref="CashMovement"/>.
/// Independent of user login state — closing this requires an explicit close action, never an
/// implicit side effect of an employee logging out.
/// </summary>
public class CashSession
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid RegisterId { get; set; }
    public Guid OpenedByUserId { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public decimal OpeningCashAmount { get; set; }
    public decimal? ClosingCountedAmount { get; set; }
    /// <summary>Opening float plus every accepted cash movement since — computed at close time.</summary>
    public decimal? ExpectedCashAmount { get; set; }
    /// <summary>ClosingCountedAmount minus ExpectedCashAmount. Positive = over, negative = short.</summary>
    public decimal? DiscrepancyAmount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public CashSessionStatus Status { get; set; }
    /// <summary>True when this session is shared by every cashier on the register rather than opened per-user.</summary>
    public bool IsSharedSession { get; set; }
    public string? Notes { get; set; }
    public bool IsSynced { get; set; }
    public int SyncVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public Store? Store { get; set; }
    public Register? Register { get; set; }
    public User? OpenedByUser { get; set; }
    public User? ClosedByUser { get; set; }
    public ICollection<CashMovement> Movements { get; set; } = new List<CashMovement>();
}
