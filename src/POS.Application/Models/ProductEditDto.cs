namespace POS.Application.Models;

public sealed class ProductEditDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public decimal Price { get; set; }
    public decimal Cost { get; set; }
    public Guid CategoryId { get; set; }
    public decimal InitialStock { get; set; }
    /// <summary>Mandatory-reason enforcement lives in POS.Web's controller; WPF may leave this null.</summary>
    public string? StockAdjustmentReason { get; set; }
    public string? ImagePath { get; set; }
    public bool IsActive { get; set; } = true;
}
