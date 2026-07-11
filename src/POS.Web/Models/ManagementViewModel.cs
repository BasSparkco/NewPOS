using POS.Application.Models;
using POS.Core.Enums;

namespace POS.Web.Models;

public sealed class ManagementViewModel
{
    public string StoreName { get; init; } = string.Empty;
    public string CurrentUsername { get; init; } = string.Empty;
    public string CurrentBaseCurrencyCode { get; init; } = "ILS";
    public Guid? StockProductFilterId { get; init; }
    public StockMovementType? StockMovementTypeFilter { get; init; }
    public DateOnly? StockFromDate { get; init; }
    public DateOnly? StockToDate { get; init; }
    public string? StockDatePreset { get; init; }
    public string StockLedgerSummary { get; init; } = "Showing latest movements for all products.";
    public StoreProfileFormViewModel StoreProfile { get; init; } = new();
    public OperationalSettingsFormViewModel OperationalSettings { get; init; } = new();
    public CurrencyPolicyFormViewModel CurrencyPolicy { get; init; } = new();
    public CreateUserFormViewModel CreateUser { get; init; } = new();
    public CreateCategoryFormViewModel CreateCategory { get; init; } = new();
    public ProductEditorFormViewModel CreateProduct { get; init; } = new();
    public IReadOnlyList<RoleOptionViewModel> Roles { get; init; } = Array.Empty<RoleOptionViewModel>();
    public IReadOnlyList<CategoryDto> Categories { get; init; } = Array.Empty<CategoryDto>();
    public IReadOnlyList<ManagementCategoryRowViewModel> CategoryRows { get; init; } = Array.Empty<ManagementCategoryRowViewModel>();
    public IReadOnlyList<ManagementUserRowViewModel> Users { get; init; } = Array.Empty<ManagementUserRowViewModel>();
    public IReadOnlyList<ManagementProductRowViewModel> Products { get; init; } = Array.Empty<ManagementProductRowViewModel>();
    public IReadOnlyList<StockMovementTypeOptionViewModel> StockMovementTypes { get; init; } = Array.Empty<StockMovementTypeOptionViewModel>();
    public IReadOnlyList<ManagementStockMovementRowViewModel> StockMovements { get; init; } = Array.Empty<ManagementStockMovementRowViewModel>();
}

public sealed class StockMovementTypeOptionViewModel
{
    public StockMovementType Type { get; init; }
    public string Label { get; init; } = string.Empty;
}

public sealed class StoreProfileFormViewModel
{
    public string Name { get; init; } = string.Empty;
    public string? Address { get; init; }
    public string? Phone { get; init; }
}

public sealed class OperationalSettingsFormViewModel
{
    public bool AllowNegativeStock { get; init; }
    public decimal LowStockThreshold { get; init; }
    public decimal DefaultTaxPercent { get; init; }
    public string? ReceiptFooterText { get; init; }
}

public sealed class CurrencyPolicyFormViewModel
{
    public Guid BaseCurrencyId { get; init; }
    public List<CurrencyRateInputViewModel> Rates { get; init; } = [];
}

public sealed class CurrencyRateInputViewModel
{
    public Guid CurrencyId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Symbol { get; init; }
    public decimal ExchangeRate { get; init; }
}

public sealed class CreateUserFormViewModel
{
    public string Username { get; init; } = string.Empty;
    public Guid RoleId { get; init; }
    public bool IsActive { get; init; }
}

public sealed class CreateCategoryFormViewModel
{
    public string Name { get; init; } = string.Empty;
}

public sealed class CategoryEditorFormViewModel
{
    public Guid CategoryId { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class UserEditFormViewModel
{
    public Guid UserId { get; init; }
    public string Username { get; init; } = string.Empty;
    public Guid RoleId { get; init; }
    public bool IsActive { get; init; }
}

public sealed class ProductEditorFormViewModel
{
    public Guid ProductId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public decimal Price { get; init; }
    public decimal Cost { get; init; }
    public Guid CategoryId { get; init; }
    public decimal InitialStock { get; init; }
    public string? ImagePath { get; init; }
    public bool IsActive { get; init; }
}

public sealed class RoleOptionViewModel
{
    public RoleOptionViewModel(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; }
    public string Name { get; }
}

public sealed class ManagementUserRowViewModel
{
    public Guid UserId { get; init; }
    public string Username { get; init; } = string.Empty;
    public Guid RoleId { get; init; }
    public string RoleName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime UpdatedAtLocal { get; init; }
    public bool IsCurrentUser { get; init; }
}

public sealed class ManagementCategoryRowViewModel
{
    public Guid CategoryId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int ProductCount { get; init; }
    public bool CanDelete => ProductCount == 0;
}

public sealed class ManagementProductRowViewModel
{
    public Guid ProductId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public decimal Price { get; init; }
    public decimal Cost { get; init; }
    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public decimal QuantityOnHand { get; init; }
    public string? ImagePath { get; init; }
    public bool IsActive { get; init; }
    public DateTime UpdatedAtLocal { get; init; }
}

public sealed class ManagementStockMovementRowViewModel
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string MovementType { get; init; } = string.Empty;
    public decimal QuantityDelta { get; init; }
    public decimal QuantityAfter { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAtLocal { get; init; }
}