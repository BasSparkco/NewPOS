namespace POS.Application.Models;

public sealed record CurrencyDto(Guid Id, string Code, string Name, string? Symbol, decimal ExchangeRate);
