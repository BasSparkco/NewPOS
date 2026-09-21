namespace POS.Core.Entities;

/// <summary>A branch/physical location owned by a <see cref="Tenant"/>.</summary>
public class Store
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public Guid BaseCurrencyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public Currency? BaseCurrency { get; set; }
    public ICollection<Device> Devices { get; set; } = new List<Device>();
}
