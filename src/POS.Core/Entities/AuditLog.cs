namespace POS.Core.Entities;

public class AuditLog
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>Null for tenant-wide events not tied to one branch.</summary>
    public Guid? StoreId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public Store? Store { get; set; }
    public User? User { get; set; }
}