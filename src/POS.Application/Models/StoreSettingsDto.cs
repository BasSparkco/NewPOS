namespace POS.Application.Models;

public sealed record StoreSettingsDto(
    bool AllowNegativeStock,
    decimal LowStockThreshold,
    decimal DefaultTaxPercent,
    string? ReceiptFooterText,
    bool UseArabicIndicDigits = false,
    // When true, product prices already include VAT and no additional tax is added at checkout.
    bool PricesIncludeVat = false);