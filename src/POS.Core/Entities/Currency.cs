namespace POS.Core.Entities;

/// <summary>
/// Exchange rate is expressed as: amount in store base = amount in this currency × (this.ExchangeRate / base.ExchangeRate).
/// The store base currency should use ExchangeRate = 1.
/// </summary>
public class Currency
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public decimal ExchangeRate { get; set; } = 1m;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
