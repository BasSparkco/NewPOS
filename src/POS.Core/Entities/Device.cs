namespace POS.Core.Entities;

/// <summary>
/// The physical machine ("Box") a cashier signs into at a given branch. A Device is bound to one
/// logical <see cref="Register"/> — the Register (not the Device) is what carries a stable number
/// and sale history, so replacing this machine's hardware never orphans that history. Old rows
/// created before Registers existed may still have a null <see cref="RegisterId"/> until the
/// startup backfill assigns one.
/// </summary>
public class Device
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid? RegisterId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int SyncVersion { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>BCrypt hash of the one-time enrollment code an admin issued when provisioning this Box. Cleared once redeemed.</summary>
    public string? EnrollmentCodeHash { get; set; }
    /// <summary>Set when a machine successfully redeems the enrollment code. Null means "provisioned but not yet enrolled."</summary>
    public DateTime? EnrolledAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// BCrypt hash of this device's persistent sync credential, issued once at enrollment (distinct
    /// from the one-time <see cref="EnrollmentCodeHash"/>). Presenting the matching plaintext secret
    /// to <c>/api/devices/token</c> authenticates the terminal itself — scoped to background
    /// synchronization only — independent of any employee's own login, so sync can keep running
    /// after that employee signs out. It never grants business-operation access (sales, refunds,
    /// administration): those always require a real user credential.
    /// </summary>
    public string? DeviceSecretHash { get; set; }

    public Tenant? Tenant { get; set; }
    public Store? Store { get; set; }
    public Register? Register { get; set; }
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}