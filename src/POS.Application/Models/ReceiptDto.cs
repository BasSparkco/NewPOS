namespace POS.Application.Models;

public sealed class ReceiptDto
{
    public string StoreName { get; init; } = string.Empty;
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateTime PaidAt { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal Total { get; init; }
    public decimal CashTendered { get; init; }
    public decimal Change { get; init; }
    public IReadOnlyList<ReceiptLineDto> Lines { get; init; } = Array.Empty<ReceiptLineDto>();
    public string? Notes { get; init; }
}

public sealed record ReceiptLineDto(string Name, decimal Quantity, decimal UnitPrice, decimal LineTotal);
