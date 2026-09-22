using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Application.Support;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;
using POS.Web.Infrastructure;
using POS.Web.Models;

namespace POS.Web.Controllers;

[Authorize(Policy = WebAuthorizationPolicies.DashboardAccess)]
public sealed class ManagementController : Controller
{
    private const string TodayPreset = "Today";
    private const string YesterdayPreset = "Yesterday";
    private const string Last7DaysPreset = "Last7Days";
    private const string Last30DaysPreset = "Last30Days";
    private const string ThisMonthPreset = "ThisMonth";

    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly IProductCatalogService _productCatalogService;
    private readonly ISettingsService _settingsService;
    private readonly ICurrencyService _currencyService;
    private readonly IAuditLogService _auditLogService;
    private readonly IUserManagementService _userManagementService;
    private readonly ICurrentSession _session;

    public ManagementController(
        IDbContextFactory<PosDbContext> dbFactory,
        IProductCatalogService productCatalogService,
        ISettingsService settingsService,
        ICurrencyService currencyService,
        IAuditLogService auditLogService,
        IUserManagementService userManagementService,
        ICurrentSession session)
    {
        _dbFactory = dbFactory;
        _productCatalogService = productCatalogService;
        _settingsService = settingsService;
        _currencyService = currencyService;
        _auditLogService = auditLogService;
        _userManagementService = userManagementService;
        _session = session;
    }

    [HttpGet]
    public async Task<IActionResult> Index(Guid? stockProductId, StockMovementType? stockMovementType, DateOnly? stockFromDate, DateOnly? stockToDate, string? stockDatePreset, bool stockDiscrepancyOnly, CancellationToken cancellationToken)
    {
        var model = await BuildViewModelAsync(stockProductId, stockMovementType, stockFromDate, stockToDate, stockDatePreset, stockDiscrepancyOnly, cancellationToken);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageSettings)]
    public async Task<IActionResult> UpdateStoreProfile(StoreProfileFormViewModel form, CancellationToken cancellationToken)
    {
        var name = form.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["ManagementError"] = "Store name is required.";
            return RedirectToAction(nameof(Index));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var store = await db.Stores
            .FirstOrDefaultAsync(s => s.Id == _session.StoreId && !s.IsDeleted, cancellationToken);

        if (store is null)
            return NotFound();

        var updatedAddress = NormalizeOptional(form.Address);
        var updatedPhone = NormalizeOptional(form.Phone);

        var changed = store.Name != name
            || store.Address != updatedAddress
            || store.Phone != updatedPhone;

        if (!changed)
        {
            TempData["ManagementSuccess"] = "Store profile is already up to date.";
            return RedirectToAction(nameof(Index));
        }

        var oldSummary = $"{store.Name} | {store.Address ?? "-"} | {store.Phone ?? "-"}";

        store.Name = name;
        store.Address = updatedAddress;
        store.Phone = updatedPhone;
        store.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        await TryWriteAuditAsync(
            "StoreProfileUpdated",
            nameof(Store),
            store.Id,
            $"{oldSummary} -> {store.Name} | {store.Address ?? "-"} | {store.Phone ?? "-"}",
            cancellationToken);

        TempData["ManagementSuccess"] = "Store profile updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageSettings)]
    public async Task<IActionResult> UpdateOperationalSettings(OperationalSettingsFormViewModel form, CancellationToken cancellationToken)
    {
        if (form.LowStockThreshold < 0)
        {
            TempData["ManagementError"] = "Low-stock threshold cannot be negative.";
            return RedirectToAction(nameof(Index));
        }

        if (form.DefaultTaxPercent < 0 || form.DefaultTaxPercent > 100)
        {
            TempData["ManagementError"] = "Default tax percent must be between 0 and 100.";
            return RedirectToAction(nameof(Index));
        }

        await _settingsService.UpdateStoreSettingsAsync(
            new StoreSettingsDto(
                form.AllowNegativeStock,
                form.LowStockThreshold,
                form.DefaultTaxPercent,
                NormalizeOptional(form.ReceiptFooterText),
                PricesIncludeVat: form.PricesIncludeVat),
            cancellationToken);

        TempData["ManagementSuccess"] = "Operational settings updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageSettings)]
    public async Task<IActionResult> UpdateCurrencyPolicy(CurrencyPolicyFormViewModel form, CancellationToken cancellationToken)
    {
        if (form.BaseCurrencyId == Guid.Empty)
        {
            TempData["ManagementError"] = "Select a base currency.";
            return RedirectToAction(nameof(Index));
        }

        var rates = form.Rates
            .Select(rate => new CurrencyRateUpdateDto(rate.CurrencyId, rate.ExchangeRate))
            .ToList();

        await _currencyService.UpdateStoreCurrencyPolicyAsync(form.BaseCurrencyId, rates, cancellationToken);

        TempData["ManagementSuccess"] = "Currency policy updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageUsers)]
    public async Task<IActionResult> CreateUser(CreateUserFormViewModel form, CancellationToken cancellationToken)
    {
        var username = form.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            TempData["ManagementError"] = "Username is required.";
            return RedirectToAction(nameof(Index));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var role = await db.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == form.RoleId && !r.IsDeleted, cancellationToken);

        if (role is null)
        {
            TempData["ManagementError"] = "Selected role was not found.";
            return RedirectToAction(nameof(Index));
        }

        var normalizedUsername = username.ToLowerInvariant();
        var exists = await db.Users.AnyAsync(
            user => user.StoreId == _session.StoreId
                && !user.IsDeleted
                && user.Username.ToLower() == normalizedUsername,
            cancellationToken);

        if (exists)
        {
            TempData["ManagementError"] = $"User '{username}' already exists in this store.";
            return RedirectToAction(nameof(Index));
        }

        var tenantId = await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == _session.StoreId && !s.IsDeleted)
            .Select(s => (Guid?)s.TenantId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");

        var generatedPassword = RandomPasswordGenerator.Generate();
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Username = username,
            NormalizedUsername = normalizedUsername.ToUpperInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(generatedPassword),
            RoleId = role.Id,
            StoreId = _session.StoreId,
            IsActive = form.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false
        };

        db.Users.Add(user);
        db.UserStoreAccesses.Add(new UserStoreAccess
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = user.Id,
            StoreId = _session.StoreId,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await TryWriteAuditAsync(
            "UserCreated",
            nameof(User),
            user.Id,
            $"Username={user.Username}; Role={role.Name}; Active={user.IsActive}",
            cancellationToken);

        TempData["ManagementSuccess"] = $"User '{user.Username}' created. Temporary password: {generatedPassword} — share this securely; it will not be shown again.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageProducts)]
    public async Task<IActionResult> CreateCategory(CreateCategoryFormViewModel form, CancellationToken cancellationToken)
    {
        var name = form.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["ManagementError"] = "Category name is required.";
            return RedirectToAction(nameof(Index));
        }

        var categories = await _productCatalogService.GetCategoriesAsync(cancellationToken);
        if (categories.Any(category => string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            TempData["ManagementError"] = $"Category '{name}' already exists.";
            return RedirectToAction(nameof(Index));
        }

        var category = await _productCatalogService.CreateCategoryAsync(name, cancellationToken);
        TempData["ManagementSuccess"] = $"Category '{category.Name}' created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageProducts)]
    public async Task<IActionResult> UpdateCategory(CategoryEditorFormViewModel form, CancellationToken cancellationToken)
    {
        if (form.CategoryId == Guid.Empty)
        {
            TempData["ManagementError"] = "Category id is required.";
            return RedirectToAction(nameof(Index));
        }

        var name = form.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["ManagementError"] = "Category name is required.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var category = await _productCatalogService.UpdateCategoryAsync(form.CategoryId, name, cancellationToken);
            TempData["ManagementSuccess"] = $"Category '{category.Name}' updated.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ManagementError"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageProducts)]
    public async Task<IActionResult> DeleteCategory(Guid categoryId, CancellationToken cancellationToken)
    {
        if (categoryId == Guid.Empty)
        {
            TempData["ManagementError"] = "Category id is required.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await _productCatalogService.DeleteCategoryAsync(categoryId, cancellationToken);
            TempData["ManagementSuccess"] = "Category deleted.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ManagementError"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageUsers)]
    public async Task<IActionResult> UpdateUser(UserEditFormViewModel form, CancellationToken cancellationToken)
    {
        var username = form.Username.Trim();
        if (form.UserId == Guid.Empty)
        {
            TempData["ManagementError"] = "User id is required.";
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            TempData["ManagementError"] = "Username is required.";
            return RedirectToAction(nameof(Index));
        }

        if (!form.IsActive && form.UserId == _session.UserId)
        {
            TempData["ManagementError"] = "You cannot deactivate the account used for the current session.";
            return RedirectToAction(nameof(Index));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == form.UserId && u.StoreId == _session.StoreId && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            TempData["ManagementError"] = "User not found.";
            return RedirectToAction(nameof(Index));
        }

        var role = await db.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == form.RoleId && !r.IsDeleted, cancellationToken);

        if (role is null)
        {
            TempData["ManagementError"] = "Selected role was not found.";
            return RedirectToAction(nameof(Index));
        }

        var normalizedUsername = username.ToLowerInvariant();
        var duplicate = await db.Users.AnyAsync(
            existing => existing.StoreId == _session.StoreId
                && existing.Id != form.UserId
                && !existing.IsDeleted
                && existing.Username.ToLower() == normalizedUsername,
            cancellationToken);

        if (duplicate)
        {
            TempData["ManagementError"] = $"Another user already uses '{username}'.";
            return RedirectToAction(nameof(Index));
        }

        var changed = user.Username != username
            || user.RoleId != role.Id
            || user.IsActive != form.IsActive;

        if (!changed)
        {
            TempData["ManagementSuccess"] = $"User '{user.Username}' is already up to date.";
            return RedirectToAction(nameof(Index));
        }

        var oldDetails = $"Username={user.Username}; RoleId={user.RoleId}; Active={user.IsActive}";

        user.Username = username;
        user.NormalizedUsername = normalizedUsername.ToUpperInvariant();
        user.RoleId = role.Id;
        user.IsActive = form.IsActive;
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        await TryWriteAuditAsync(
            "UserUpdated",
            nameof(User),
            user.Id,
            $"{oldDetails} -> Username={user.Username}; Role={role.Name}; Active={user.IsActive}",
            cancellationToken);

        TempData["ManagementSuccess"] = $"User '{user.Username}' updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageUsers)]
    public async Task<IActionResult> ResetUserPassword(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            TempData["ManagementError"] = "User id is required.";
            return RedirectToAction(nameof(Index));
        }

        var (success, error, generatedPassword) = await _userManagementService.ResetUserPasswordAsync(userId, cancellationToken);
        if (!success || generatedPassword is null)
        {
            TempData["ManagementError"] = error ?? "Could not reset password.";
            return RedirectToAction(nameof(Index));
        }

        TempData["ManagementSuccess"] = $"Temporary password: {generatedPassword} — share this securely; it will not be shown again.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageProducts)]
    public async Task<IActionResult> CreateProduct(ProductEditorFormViewModel form, CancellationToken cancellationToken)
    {
        var validationError = ValidateProductForm(form, requireProductId: false);
        if (validationError is not null)
        {
            TempData["ManagementError"] = validationError;
            return RedirectToAction(nameof(Index));
        }

        await _productCatalogService.CreateProductAsync(new ProductEditDto
        {
            Name = form.Name.Trim(),
            Barcode = NormalizeOptional(form.Barcode),
            Price = form.Price,
            Cost = form.Cost,
            CategoryId = form.CategoryId,
            InitialStock = Math.Max(0m, form.InitialStock),
            ImagePath = NormalizeOptional(form.ImagePath),
            IsActive = form.IsActive
        }, cancellationToken);

        TempData["ManagementSuccess"] = $"Product '{form.Name.Trim()}' created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageProducts)]
    public async Task<IActionResult> UpdateProduct(ProductEditorFormViewModel form, CancellationToken cancellationToken)
    {
        var validationError = ValidateProductForm(form, requireProductId: true);
        if (validationError is not null)
        {
            TempData["ManagementError"] = validationError;
            return RedirectToAction(nameof(Index));
        }

        var existing = await _productCatalogService.GetProductForEditAsync(form.ProductId, cancellationToken);
        if (existing is null)
        {
            TempData["ManagementError"] = "Product not found.";
            return RedirectToAction(nameof(Index));
        }

        var requestedStock = Math.Max(0m, form.InitialStock);
        if (requestedStock != existing.InitialStock && string.IsNullOrWhiteSpace(form.StockAdjustmentReason))
        {
            TempData["ManagementError"] = "A reason is required when changing a product's stock.";
            return RedirectToAction(nameof(Index));
        }

        existing.Name = form.Name.Trim();
        existing.Barcode = NormalizeOptional(form.Barcode);
        existing.Price = form.Price;
        existing.Cost = form.Cost;
        existing.CategoryId = form.CategoryId;
        existing.InitialStock = requestedStock;
        existing.StockAdjustmentReason = NormalizeOptional(form.StockAdjustmentReason);
        existing.ImagePath = NormalizeOptional(form.ImagePath);
        existing.IsActive = form.IsActive;

        await _productCatalogService.UpdateProductAsync(existing, cancellationToken);

        TempData["ManagementSuccess"] = $"Product '{existing.Name}' updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = WebAuthorizationPolicies.ManageProducts)]
    public async Task<IActionResult> DeleteProduct(Guid productId, CancellationToken cancellationToken)
    {
        if (productId == Guid.Empty)
        {
            TempData["ManagementError"] = "Product id is required.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await _productCatalogService.DeleteProductAsync(productId, cancellationToken);
            TempData["ManagementSuccess"] = "Product deleted.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ManagementError"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<ManagementViewModel> BuildViewModelAsync(Guid? stockProductId, StockMovementType? stockMovementType, DateOnly? stockFromDate, DateOnly? stockToDate, string? stockDatePreset, bool stockDiscrepancyOnly, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var resolvedDateFilters = ResolveDateFilters(stockFromDate, stockToDate, stockDatePreset);
        stockDatePreset = resolvedDateFilters.DatePreset;
        var normalizedDateRange = NormalizeDateRange(resolvedDateFilters.FromDate, resolvedDateFilters.ToDate);
        stockFromDate = normalizedDateRange.FromDate;
        stockToDate = normalizedDateRange.ToDate;

        var store = await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == _session.StoreId && !s.IsDeleted)
            .Select(s => new { s.Id, s.Name, s.Address, s.Phone })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");

        var settings = await _settingsService.GetStoreSettingsAsync(cancellationToken);
        var currencyPolicy = await _currencyService.GetStoreCurrencyPolicyAsync(cancellationToken);
        var categories = await _productCatalogService.GetCategoriesAsync(cancellationToken);
        var stockMovements = await _productCatalogService.GetStockMovementsAsync(stockProductId, cancellationToken);

        if (stockMovementType.HasValue)
            stockMovements = stockMovements.Where(movement => movement.Type == stockMovementType.Value).ToList();

        if (stockFromDate.HasValue)
            stockMovements = stockMovements.Where(movement => DateOnly.FromDateTime(movement.CreatedAt.ToLocalTime()) >= stockFromDate.Value).ToList();

        if (stockToDate.HasValue)
            stockMovements = stockMovements.Where(movement => DateOnly.FromDateTime(movement.CreatedAt.ToLocalTime()) <= stockToDate.Value).ToList();

        if (stockDiscrepancyOnly)
            stockMovements = stockMovements.Where(movement => movement.IsDiscrepancy).ToList();

        var roles = await db.Roles
            .AsNoTracking()
            .Where(role => !role.IsDeleted)
            .OrderBy(role => role.Name)
            .Select(role => new RoleOptionViewModel(role.Id, role.Name))
            .ToListAsync(cancellationToken);

        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.StoreId == _session.StoreId && !user.IsDeleted)
            .Join(
                db.Roles.AsNoTracking().Where(role => !role.IsDeleted),
                user => user.RoleId,
                role => role.Id,
                (user, role) => new ManagementUserRowViewModel
                {
                    UserId = user.Id,
                    Username = user.Username,
                    RoleId = role.Id,
                    RoleName = role.Name,
                    IsActive = user.IsActive,
                    UpdatedAtLocal = user.UpdatedAt.ToLocalTime(),
                    IsCurrentUser = user.Id == _session.UserId
                })
            .OrderByDescending(user => user.IsCurrentUser)
            .ThenByDescending(user => user.IsActive)
            .ThenBy(user => user.Username)
            .ToListAsync(cancellationToken);

        var inventoryByProduct = await db.Inventories
            .AsNoTracking()
            .Where(inventory => inventory.StoreId == _session.StoreId && !inventory.IsDeleted)
            .ToDictionaryAsync(inventory => inventory.ProductId, inventory => inventory.Quantity, cancellationToken);

        var productCountsByCategory = await db.Products
            .AsNoTracking()
            .Where(product => !product.IsDeleted)
            .GroupBy(product => product.CategoryId)
            .Select(group => new { CategoryId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.CategoryId, entry => entry.Count, cancellationToken);

        var products = await db.Products
            .AsNoTracking()
            .Where(product => !product.IsDeleted)
            .Join(
                db.Categories.AsNoTracking().Where(category => !category.IsDeleted),
                product => product.CategoryId,
                category => category.Id,
                (product, category) => new ManagementProductRowViewModel
                {
                    ProductId = product.Id,
                    Name = product.Name,
                    Barcode = product.Barcode,
                    Price = product.Price,
                    Cost = product.Cost,
                    CategoryId = category.Id,
                    CategoryName = category.Name,
                    QuantityOnHand = inventoryByProduct.GetValueOrDefault(product.Id),
                    ImagePath = product.ImagePath,
                    IsActive = product.IsActive,
                    UpdatedAtLocal = product.UpdatedAt.ToLocalTime()
                })
            .OrderByDescending(product => product.IsActive)
            .ThenBy(product => product.Name)
            .ToListAsync(cancellationToken);

        var selectedStockProductName = stockProductId.HasValue
            ? products.FirstOrDefault(product => product.ProductId == stockProductId.Value)?.Name
            : null;

        var movementTypeLabel = stockMovementType switch
        {
            StockMovementType.OpeningStock => "opening-stock",
            StockMovementType.ManualSetAdjustment => "manual-adjustment",
            StockMovementType.Sale => "sale",
            StockMovementType.Refund => "refund",
            _ => null
        };

        var stockLedgerSummary = BuildStockLedgerSummary(selectedStockProductName, movementTypeLabel, stockFromDate, stockToDate);

        return new ManagementViewModel
        {
            StockProductFilterId = stockProductId,
            StockMovementTypeFilter = stockMovementType,
            StockFromDate = stockFromDate,
            StockToDate = stockToDate,
            StockDatePreset = stockDatePreset,
            StockDiscrepancyOnly = stockDiscrepancyOnly,
            StockLedgerSummary = stockLedgerSummary,
            StoreProfile = new StoreProfileFormViewModel
            {
                Name = store.Name,
                Address = store.Address,
                Phone = store.Phone
            },
            OperationalSettings = new OperationalSettingsFormViewModel
            {
                AllowNegativeStock = settings.AllowNegativeStock,
                LowStockThreshold = settings.LowStockThreshold,
                DefaultTaxPercent = settings.DefaultTaxPercent,
                PricesIncludeVat = settings.PricesIncludeVat,
                ReceiptFooterText = settings.ReceiptFooterText
            },
            CurrencyPolicy = new CurrencyPolicyFormViewModel
            {
                BaseCurrencyId = currencyPolicy.BaseCurrencyId,
                Rates = currencyPolicy.Currencies
                    .Select(currency => new CurrencyRateInputViewModel
                    {
                        CurrencyId = currency.Id,
                        Code = currency.Code,
                        Name = currency.Name,
                        Symbol = currency.Symbol,
                        ExchangeRate = currency.ExchangeRate
                    })
                    .ToList()
            },
            CreateUser = new CreateUserFormViewModel
            {
                IsActive = true,
                RoleId = roles.FirstOrDefault()?.Id ?? Guid.Empty
            },
            CreateCategory = new CreateCategoryFormViewModel(),
            CreateProduct = new ProductEditorFormViewModel
            {
                CategoryId = categories.FirstOrDefault()?.Id ?? Guid.Empty,
                IsActive = true
            },
            Roles = roles,
            Categories = categories,
            CategoryRows = categories
                .Select(category => new ManagementCategoryRowViewModel
                {
                    CategoryId = category.Id,
                    Name = category.Name,
                    ProductCount = productCountsByCategory.GetValueOrDefault(category.Id)
                })
                .ToList(),
            Users = users,
            Products = products,
            StockMovementTypes = Enum.GetValues<StockMovementType>()
                .Select(type => new StockMovementTypeOptionViewModel
                {
                    Type = type,
                    Label = type switch
                    {
                        StockMovementType.OpeningStock => "Opening stock",
                        StockMovementType.ManualSetAdjustment => "Manual adjustment",
                        StockMovementType.Sale => "Sale",
                        StockMovementType.Refund => "Refund",
                        _ => type.ToString()
                    }
                })
                .ToList(),
            StockMovements = stockMovements
                .Take(12)
                .Select(movement => new ManagementStockMovementRowViewModel
                {
                    ProductId = movement.ProductId,
                    ProductName = movement.ProductName,
                    MovementType = movement.Type.ToString(),
                    QuantityDelta = movement.QuantityDelta,
                    QuantityAfter = movement.QuantityAfter,
                    Reference = movement.Reference,
                    Notes = movement.Notes,
                    IsDiscrepancy = movement.IsDiscrepancy,
                    CreatedAtLocal = movement.CreatedAt.ToLocalTime()
                })
                .ToList(),
            CurrentUsername = _session.Username,
            StoreName = store.Name,
            CurrentBaseCurrencyCode = currencyPolicy.Currencies.FirstOrDefault(currency => currency.Id == currencyPolicy.BaseCurrencyId)?.Code ?? _session.BaseCurrencyCode
        };
    }

    private static (DateOnly? FromDate, DateOnly? ToDate) NormalizeDateRange(DateOnly? fromDate, DateOnly? toDate)
    {
        if (fromDate.HasValue && toDate.HasValue && fromDate > toDate)
            return (toDate, fromDate);

        return (fromDate, toDate);
    }

    private static (DateOnly? FromDate, DateOnly? ToDate, string? DatePreset) ResolveDateFilters(DateOnly? fromDate, DateOnly? toDate, string? datePreset)
    {
        var normalizedPreset = NormalizeDatePreset(datePreset);
        if (normalizedPreset is null)
            return (fromDate, toDate, null);

        var today = DateOnly.FromDateTime(DateTime.Now);
        return normalizedPreset switch
        {
            TodayPreset => (today, today, TodayPreset),
            YesterdayPreset => (today.AddDays(-1), today.AddDays(-1), YesterdayPreset),
            Last7DaysPreset => (today.AddDays(-6), today, Last7DaysPreset),
            Last30DaysPreset => (today.AddDays(-29), today, Last30DaysPreset),
            ThisMonthPreset => (new DateOnly(today.Year, today.Month, 1), today, ThisMonthPreset),
            _ => (fromDate, toDate, null)
        };
    }

    private static string? NormalizeDatePreset(string? datePreset)
    {
        if (string.IsNullOrWhiteSpace(datePreset))
            return null;

        return datePreset switch
        {
            TodayPreset => TodayPreset,
            YesterdayPreset => YesterdayPreset,
            Last7DaysPreset => Last7DaysPreset,
            Last30DaysPreset => Last30DaysPreset,
            ThisMonthPreset => ThisMonthPreset,
            _ => null
        };
    }

    private static string BuildStockLedgerSummary(string? productName, string? movementTypeLabel, DateOnly? fromDate, DateOnly? toDate)
    {
        var summary = "Showing latest";

        if (!string.IsNullOrWhiteSpace(movementTypeLabel))
            summary += $" {movementTypeLabel}";

        summary += " movements";
        summary += string.IsNullOrWhiteSpace(productName)
            ? " for all products"
            : $" for {productName}";

        summary += DescribeDateRange(fromDate, toDate);
        return summary + ".";
    }

    private static string DescribeDateRange(DateOnly? fromDate, DateOnly? toDate)
    {
        if (fromDate.HasValue && toDate.HasValue && fromDate.Value == toDate.Value)
            return $" on {fromDate.Value:yyyy-MM-dd}";

        if (fromDate.HasValue && toDate.HasValue)
            return $" between {fromDate.Value:yyyy-MM-dd} and {toDate.Value:yyyy-MM-dd}";

        if (fromDate.HasValue)
            return $" from {fromDate.Value:yyyy-MM-dd}";

        if (toDate.HasValue)
            return $" through {toDate.Value:yyyy-MM-dd}";

        return string.Empty;
    }

    private static string? ValidateProductForm(ProductEditorFormViewModel form, bool requireProductId)
    {
        if (requireProductId && form.ProductId == Guid.Empty)
            return "Product id is required.";

        if (string.IsNullOrWhiteSpace(form.Name))
            return "Product name is required.";

        if (form.CategoryId == Guid.Empty)
            return "Select a category.";

        if (form.Price < 0)
            return "Price must be zero or greater.";

        if (form.Cost < 0)
            return "Cost must be zero or greater.";

        if (form.InitialStock < 0)
            return "Stock must be zero or greater.";

        return null;
    }

    private async Task TryWriteAuditAsync(string action, string entityName, Guid? entityId, string? details, CancellationToken cancellationToken)
    {
        try
        {
            await _auditLogService.WriteAsync(action, entityName, entityId, details, cancellationToken);
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}