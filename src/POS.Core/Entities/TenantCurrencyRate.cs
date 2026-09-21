namespace POS.Core.Entities;

/// <summary>
/// A tenant's own exchange rate for a (globally shared, immutable) <see cref="Currency"/> reference row.
/// Rate is expressed the same way the old Currency.ExchangeRate was: amount in tenant base =
/// amount in this currency × (this.ExchangeRate / baseRate.ExchangeRate). The tenant's base currency
/// uses ExchangeRate = 1. Scoped per tenant so one tenant changing its base currency can never
/// re-normalize another tenant's rates.
/// </summary>
public class TenantCurrencyRate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CurrencyId { get; set; }
    public decimal ExchangeRate { get; set; } = 1m;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public Tenant? Tenant { get; set; }
    public Currency? Currency { get; set; }
}
