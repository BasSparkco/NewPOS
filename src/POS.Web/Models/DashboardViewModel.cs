namespace POS.Web.Models;

public sealed class DashboardViewModel
{
    public DateTime SelectedDate { get; init; }
    public string StoreName { get; init; } = string.Empty;
    public string? StoreAddress { get; init; }
    public string? StorePhone { get; init; }
    public string Username { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public string CurrencyCode { get; init; } = "ILS";
    public string? CurrencySymbol { get; init; }
    public DashboardSummaryViewModel Summary { get; init; } = new();
    public IReadOnlyList<DashboardTopProductViewModel> TopProducts { get; init; } = Array.Empty<DashboardTopProductViewModel>();
    public IReadOnlyList<DashboardInvoiceRowViewModel> RecentInvoices { get; init; } = Array.Empty<DashboardInvoiceRowViewModel>();
    public IReadOnlyList<DashboardInventoryRowViewModel> LowStockItems { get; init; } = Array.Empty<DashboardInventoryRowViewModel>();
    public IReadOnlyList<DashboardUserRowViewModel> Users { get; init; } = Array.Empty<DashboardUserRowViewModel>();

    public string CurrencyDisplay => string.IsNullOrWhiteSpace(CurrencySymbol) ? CurrencyCode : CurrencySymbol;
}

public sealed class DashboardSummaryViewModel
{
    public decimal Revenue { get; init; }
    public int InvoiceCount { get; init; }
    public decimal ItemsSold { get; init; }
    public decimal AverageSale { get; init; }
    public int ActiveProducts { get; init; }
    public int LowStockCount { get; init; }
    public int ActiveUsers { get; init; }
}

public sealed class DashboardTopProductViewModel
{
    public int Rank { get; set; }
    public string ProductName { get; init; } = string.Empty;
    public decimal QuantitySold { get; init; }
    public decimal Revenue { get; init; }
}

public sealed class DashboardInvoiceRowViewModel
{
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateTime UpdatedAtLocal { get; init; }
    public decimal TotalAmount { get; init; }
}

public sealed class DashboardInventoryRowViewModel
{
    public string ProductName { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal Threshold { get; init; }
    public decimal Price { get; init; }
    public DateTime UpdatedAtLocal { get; init; }
}

public sealed class DashboardUserRowViewModel
{
    public string Username { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime UpdatedAtLocal { get; init; }
}