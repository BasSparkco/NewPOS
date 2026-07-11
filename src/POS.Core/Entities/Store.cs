namespace POS.Core.Entities;

public class Store
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public Guid BaseCurrencyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Currency? BaseCurrency { get; set; }
    public ICollection<Device> Devices { get; set; } = new List<Device>();
}
