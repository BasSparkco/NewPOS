namespace POS.Core.Entities;

public class Setting
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Store? Store { get; set; }
}