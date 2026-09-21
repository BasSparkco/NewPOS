namespace POS.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Username { get; set; } = string.Empty;
    /// <summary>Uppercase-invariant projection of <see cref="Username"/>; uniqueness is enforced on (TenantId, NormalizedUsername).</summary>
    public string NormalizedUsername { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public Guid RoleId { get; set; }
    /// <summary>Default/home store. Not the sole authorization mechanism — see <see cref="UserStoreAccess"/> for the full set of stores this user may work at.</summary>
    public Guid StoreId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public Role? Role { get; set; }
    public Store? Store { get; set; }
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
    public ICollection<UserStoreAccess> StoreAccesses { get; set; } = new List<UserStoreAccess>();
}
