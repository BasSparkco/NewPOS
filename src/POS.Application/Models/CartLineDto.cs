namespace POS.Application.Models;

public sealed record CartLineDto(
    Guid LineId,
    Guid ProductId,
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal LineTotal,
    string? ImagePath = null)
{
    // Convenience: display name matches the HTML design
    public string Name => ProductName;
}
