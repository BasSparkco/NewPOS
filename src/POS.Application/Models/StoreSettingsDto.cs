namespace POS.Application.Models;

public sealed record StoreSettingsDto(
    bool AllowNegativeStock,
    decimal LowStockThreshold,
    decimal DefaultTaxPercent,
    string? ReceiptFooterText);