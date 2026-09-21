namespace POS.Core.Entities;

public class Role
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int PermissionsMask { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public ICollection<User> Users { get; set; } = new List<User>();
}
