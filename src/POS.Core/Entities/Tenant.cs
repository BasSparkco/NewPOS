using POS.Core.Enums;

namespace POS.Core.Entities;

public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Lowercase, URL/login-safe identity used for tenant resolution at sign-in. Immutable identity — changing Name must not change this.</summary>
    public string NormalizedSlug { get; set; } = string.Empty;
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public ICollection<Store> Stores { get; set; } = new List<Store>();
}
