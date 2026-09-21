namespace POS.Core.Entities;

/// <summary>The register/terminal ("Box") a cashier signs into at a given branch.</summary>
public class Device
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
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

    public Tenant? Tenant { get; set; }
    public Store? Store { get; set; }
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}