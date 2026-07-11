namespace POS.Application.Models;

public sealed record StoreCurrencyPolicyDto(
    Guid StoreId,
    string StoreName,
    Guid BaseCurrencyId,
    IReadOnlyList<CurrencyDto> Currencies);