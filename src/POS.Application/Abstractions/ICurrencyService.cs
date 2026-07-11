using POS.Application.Models;

namespace POS.Application.Abstractions;

public interface ICurrencyService
{
    /// <summary>Active currencies ordered by code (for policy / future UI).</summary>
    Task<IReadOnlyList<CurrencyDto>> GetActiveCurrenciesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the current store's base-currency policy and editable exchange-rate list.</summary>
    Task<StoreCurrencyPolicyDto> GetStoreCurrencyPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts <paramref name="amount"/> expressed in <paramref name="fromCurrencyCode"/> into the current store's base currency.
    /// </summary>
    Task<decimal> ConvertToStoreBaseAsync(decimal amount, string fromCurrencyCode,
        CancellationToken cancellationToken = default);

    /// <summary>Updates exchange rates and optionally changes the store base currency.</summary>
    Task UpdateStoreCurrencyPolicyAsync(Guid baseCurrencyId, IReadOnlyList<CurrencyRateUpdateDto> rates,
        CancellationToken cancellationToken = default);
}
