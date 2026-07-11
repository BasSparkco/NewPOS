namespace POS.Application.Models;

public sealed record CurrencyRateUpdateDto(Guid CurrencyId, decimal ExchangeRate);