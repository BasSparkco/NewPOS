namespace POS.Core.Entities;

/// <summary>Explicit grant letting a tenant user work at a given store (branch), beyond their default <see cref="User.StoreId"/>.</summary>
public class UserStoreAccess
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid StoreId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
    public Store? Store { get; set; }
}
