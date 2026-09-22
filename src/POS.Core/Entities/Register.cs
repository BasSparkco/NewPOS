namespace POS.Core.Entities;

/// <summary>
/// The logical till/register at a branch — a stable identity (number, name, history) that survives
/// the physical machine being replaced. A <see cref="Device"/> ("Box") is the physical machine that
/// is currently bound to a Register; invoices and cash sessions key off the Register, not the Device,
/// so replacing a till's computer does not orphan its sales history or numbering.
/// </summary>
public class Register
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    /// <summary>Stable, store-scoped register number (e.g. register 1, 2, 3) shown to cashiers/reports.</summary>
    public int Number { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int SyncVersion { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public Store? Store { get; set; }
    public ICollection<Device> Devices { get; set; } = new List<Device>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
    public ICollection<CashSession> CashSessions { get; set; } = new List<CashSession>();
}
