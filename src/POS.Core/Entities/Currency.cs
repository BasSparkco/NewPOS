namespace POS.Core.Entities;

/// <summary>
/// Global, immutable-ish ISO currency reference data shared across all tenants (Code/Name/Symbol only).
/// Per-tenant exchange rates and base-currency policy live in <see cref="TenantCurrencyRate"/> instead,
/// so one tenant's rate changes can never affect another tenant.
/// </summary>
public class Currency
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
