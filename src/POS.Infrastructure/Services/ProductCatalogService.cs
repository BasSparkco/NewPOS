using System.Globalization;
using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class ProductCatalogService : IProductCatalogService
{
    private const string LowStockThresholdKey = "LowStockThreshold";
    private const decimal DefaultLowStockThreshold = 5m;

    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly IAuditLogService _auditLogService;
    private readonly ICurrentSession _session;

    public ProductCatalogService(IDbContextFactory<PosDbContext> dbFactory, ICurrentSession session, IAuditLogService auditLogService)
    {
        _dbFactory = dbFactory;
        _auditLogService = auditLogService;
        _session = session;
    }

    private async Task<Guid> GetTenantIdAsync(PosDbContext db, CancellationToken cancellationToken) =>
        await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == _session.StoreId && !s.IsDeleted)
            .Select(s => (Guid?)s.TenantId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.Categories
            .AsNoTracking()
            .Where(c => !c.IsDeleted)
            .OrderBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name))
            .ToListAsync(cancellationToken);
        return list;
    }

    public async Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = await GetTenantIdAsync(db, cancellationToken);
        var now = DateTime.UtcNow;
        var entity = new Category
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Categories.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new CategoryDto(entity.Id, entity.Name);
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Category not found.");

        var normalizedName = name.Trim();
        var duplicateExists = await db.Categories.AnyAsync(
            candidate => candidate.Id != id
                && !candidate.IsDeleted
                && candidate.Name.ToLower() == normalizedName.ToLower(),
            cancellationToken);

        if (duplicateExists)
            throw new InvalidOperationException($"Category '{normalizedName}' already exists.");

        var oldName = category.Name;
        if (string.Equals(oldName, normalizedName, StringComparison.Ordinal))
            return new CategoryDto(category.Id, category.Name);

        category.Name = normalizedName;
        category.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "CategoryUpdated",
            "Category",
            category.Id,
            $"Renamed category '{oldName}' -> '{category.Name}'.",
            cancellationToken);

        return new CategoryDto(category.Id, category.Name);
    }

    public async Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Category not found.");

        var inUse = await db.Products.AnyAsync(product => product.CategoryId == id && !product.IsDeleted, cancellationToken);
        if (inUse)
            throw new InvalidOperationException("Category cannot be deleted while products still reference it.");

        category.IsDeleted = true;
        category.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "CategoryDeleted",
            "Category",
            category.Id,
            $"Soft-deleted category '{category.Name}'.",
            cancellationToken);
    }

    public async Task<IReadOnlyList<ProductListItemDto>> SearchProductsAsync(string? query, Guid? categoryId = null, CancellationToken cancellationToken = default, bool includeInactive = false)
    {
        var storeId = _session.StoreId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.Products
            .AsNoTracking()
            .Where(p => !p.IsDeleted);

        if (!includeInactive)
            q = q.Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            q = q.Where(p => p.Name.Contains(term) || (p.Barcode != null && p.Barcode == term));
        }

        if (categoryId.HasValue)
            q = q.Where(p => p.CategoryId == categoryId.Value);

        var withCategory = q
            .OrderBy(p => p.Name)
            .Select(p => new { Product = p, CategoryName = p.Category!.Name });

        if (!includeInactive)
            withCategory = withCategory.Take(200);

        var products = await withCategory.ToListAsync(cancellationToken);

        if (products.Count == 0)
            return Array.Empty<ProductListItemDto>();

        var ids = products.Select(p => p.Product.Id).ToList();
        var qtyByProduct = await db.Inventories
            .AsNoTracking()
            .Where(i => ids.Contains(i.ProductId) && i.StoreId == storeId && !i.IsDeleted)
            .ToDictionaryAsync(i => i.ProductId, i => i.Quantity, cancellationToken);

        var lowStockThreshold = await GetLowStockThresholdAsync(db, storeId, cancellationToken);

        return products
            .Select(p => new ProductListItemDto(
                p.Product.Id,
                p.Product.Name,
                p.Product.Barcode,
                p.Product.Price,
                qtyByProduct.GetValueOrDefault(p.Product.Id),
                qtyByProduct.GetValueOrDefault(p.Product.Id) <= lowStockThreshold,
                p.Product.ImagePath,
                p.Product.CategoryId,
                p.CategoryName,
                p.Product.IsActive))
            .ToList();
    }

    public async Task<ProductEditDto?> GetProductForEditAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var storeId = _session.StoreId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, cancellationToken);
        if (p is null)
            return null;

        var qty = await db.Inventories
            .AsNoTracking()
            .Where(i => i.ProductId == id && i.StoreId == storeId && !i.IsDeleted)
            .Select(i => (decimal?)i.Quantity)
            .FirstOrDefaultAsync(cancellationToken) ?? 0m;

        return new ProductEditDto
        {
            Id = p.Id,
            Name = p.Name,
            Barcode = p.Barcode,
            Price = p.Price,
            Cost = p.Cost,
            CategoryId = p.CategoryId,
            InitialStock = qty,
            ImagePath = p.ImagePath,
            IsActive = p.IsActive
        };
    }

    public async Task<IReadOnlyList<StockMovementDto>> GetStockMovementsAsync(Guid? productId = null, CancellationToken cancellationToken = default)
    {
        var storeId = _session.StoreId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var query = db.StockMovements
            .AsNoTracking()
            .Where(m => m.StoreId == storeId && !m.IsDeleted);

        if (productId is not null)
            query = query.Where(m => m.ProductId == productId.Value);

        return await query
            .Join(db.Products.AsNoTracking(),
                m => m.ProductId,
                p => p.Id,
                (m, p) => new { Movement = m, ProductName = p.Name })
            .OrderByDescending(entry => entry.Movement.CreatedAt)
            .Select(entry => new StockMovementDto(
                entry.Movement.Id,
                entry.Movement.ProductId,
                entry.ProductName,
                entry.Movement.Type,
                entry.Movement.QuantityDelta,
                entry.Movement.QuantityAfter,
                entry.Movement.Reference,
                entry.Movement.Notes,
                entry.Movement.IsDiscrepancy,
                entry.Movement.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<ProductEditDto> CreateProductAsync(ProductEditDto input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ArgumentException("Product name is required.", nameof(input));

        var storeId = _session.StoreId;
        var now = DateTime.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = await GetTenantIdAsync(db, cancellationToken);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = input.Name.Trim(),
            Barcode = string.IsNullOrWhiteSpace(input.Barcode) ? null : input.Barcode.Trim(),
            Price = input.Price,
            Cost = input.Cost,
            CategoryId = input.CategoryId,
            IsWeighted = false,
            IsActive = input.IsActive,
            ImagePath = string.IsNullOrWhiteSpace(input.ImagePath) ? null : input.ImagePath.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Products.Add(product);

        var inv = new Inventory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProductId = product.Id,
            StoreId = storeId,
            Quantity = input.InitialStock < 0 ? 0 : input.InitialStock,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Inventories.Add(inv);

        if (inv.Quantity != 0)
        {
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = product.Id,
                StoreId = storeId,
                InventoryId = inv.Id,
                UserId = _session.UserId == Guid.Empty ? null : _session.UserId,
                Type = StockMovementType.OpeningStock,
                QuantityDelta = inv.Quantity,
                QuantityAfter = inv.Quantity,
                Reference = "PRODUCT_CREATE",
                Notes = "Initial stock on product creation.",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "ProductCreated",
            "Product",
            product.Id,
            $"Created product '{product.Name}' at price {product.Price:0.##} with opening stock {inv.Quantity:0.####}.",
            cancellationToken);

        input.Id = product.Id;
        return input;
    }

    public async Task<ProductEditDto> UpdateProductAsync(ProductEditDto input, CancellationToken cancellationToken = default)
    {
        var storeId = _session.StoreId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var tenantId = await GetTenantIdAsync(db, cancellationToken);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == input.Id && !p.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Product not found.");

        var oldName = product.Name;
        var oldPrice = product.Price;
        var oldBarcode = product.Barcode;
        var oldCategoryId = product.CategoryId;
        var oldIsActive = product.IsActive;

        product.Name = input.Name.Trim();
        product.Barcode = string.IsNullOrWhiteSpace(input.Barcode) ? null : input.Barcode.Trim();
        product.Price = input.Price;
        product.Cost = input.Cost;
        product.CategoryId = input.CategoryId;
        product.IsActive = input.IsActive;
        product.ImagePath = string.IsNullOrWhiteSpace(input.ImagePath) ? null : input.ImagePath.Trim();
        product.UpdatedAt = DateTime.UtcNow;

        var inv = await db.Inventories.FirstOrDefaultAsync(
            i => i.ProductId == product.Id && i.StoreId == storeId && !i.IsDeleted,
            cancellationToken);

        var now = DateTime.UtcNow;
        decimal previousQuantity;
        decimal currentQuantity;
        Guid inventoryId;
        if (inv is null)
        {
            inv = new Inventory
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = product.Id,
                StoreId = storeId,
                Quantity = input.InitialStock < 0 ? 0 : input.InitialStock,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Inventories.Add(inv);
            previousQuantity = 0m;
            currentQuantity = inv.Quantity;
            inventoryId = inv.Id;

            if (inv.Quantity != 0)
            {
                db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ProductId = product.Id,
                    StoreId = storeId,
                    InventoryId = inv.Id,
                    UserId = _session.UserId == Guid.Empty ? null : _session.UserId,
                    Type = StockMovementType.OpeningStock,
                    QuantityDelta = inv.Quantity,
                    QuantityAfter = inv.Quantity,
                    Reference = "PRODUCT_EDIT_CREATE_INVENTORY",
                    Notes = "Initial stock created while editing product.",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }
        else
        {
            previousQuantity = inv.Quantity;
            var targetQuantity = input.InitialStock < 0 ? 0 : input.InitialStock;
            var delta = targetQuantity - previousQuantity;
            inventoryId = inv.Id;

            if (delta != 0)
            {
                // Apply as an atomic DB-level increment derived from what the editor saw, not a blind
                // absolute overwrite, so a concurrent invoice reconciliation touching this same product
                // (e.g. an offline device's sale reconnecting at the same moment) is combined with this
                // adjustment instead of silently discarded — the balance is derived from both accepted
                // effects, matching tenant.md's "do not overwrite concurrent balances" rule.
                await db.Inventories
                    .Where(i => i.Id == inv.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.Quantity, i => i.Quantity + delta)
                        .SetProperty(i => i.UpdatedAt, now), cancellationToken);

                currentQuantity = await db.Inventories
                    .Where(i => i.Id == inv.Id)
                    .Select(i => i.Quantity)
                    .FirstAsync(cancellationToken);

                inv.Quantity = currentQuantity;
                inv.UpdatedAt = now;
                db.Entry(inv).Property(i => i.Quantity).IsModified = false;
                db.Entry(inv).Property(i => i.UpdatedAt).IsModified = false;

                var reason = string.IsNullOrWhiteSpace(input.StockAdjustmentReason)
                    ? null
                    : input.StockAdjustmentReason.Trim();

                db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ProductId = product.Id,
                    StoreId = storeId,
                    InventoryId = inv.Id,
                    UserId = _session.UserId == Guid.Empty ? null : _session.UserId,
                    Type = StockMovementType.ManualSetAdjustment,
                    QuantityDelta = delta,
                    QuantityAfter = currentQuantity,
                    Reference = "PRODUCT_EDIT",
                    Notes = reason is null
                        ? "Inventory manually set from product editor."
                        : $"Inventory manually set from product editor. Reason: {reason}",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                currentQuantity = previousQuantity;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await TryWriteAuditAsync(
            "ProductUpdated",
            "Product",
            product.Id,
            $"Updated product '{oldName}' -> '{product.Name}', price {oldPrice:0.##} -> {product.Price:0.##}, barcode '{oldBarcode ?? "-"}' -> '{product.Barcode ?? "-"}', category {oldCategoryId} -> {product.CategoryId}, active {oldIsActive} -> {product.IsActive}.",
            cancellationToken);

        if (previousQuantity != currentQuantity)
        {
            var reasonSuffix = string.IsNullOrWhiteSpace(input.StockAdjustmentReason)
                ? string.Empty
                : $" Reason: {input.StockAdjustmentReason.Trim()}.";

            await TryWriteAuditAsync(
                "ManualStockAdjusted",
                "Inventory",
                inventoryId,
                $"Product '{product.Name}' stock manually set {previousQuantity:0.####} -> {currentQuantity:0.####}.{reasonSuffix}",
                cancellationToken);
        }

        return input;
    }

    public async Task DeleteProductAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Product not found.");

        product.IsDeleted = true;
        product.IsActive = false;
        product.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "ProductDeleted",
            "Product",
            product.Id,
            $"Soft-deleted product '{product.Name}'.",
            cancellationToken);
    }

    private static async Task<decimal> GetLowStockThresholdAsync(PosDbContext db, Guid storeId, CancellationToken cancellationToken)
    {
        var raw = await db.Settings
            .AsNoTracking()
            .Where(s => s.StoreId == storeId && s.Key == LowStockThresholdKey && !s.IsDeleted)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var threshold)
            ? threshold
            : DefaultLowStockThreshold;
    }

    private async Task TryWriteAuditAsync(string action, string entityName, Guid? entityId, string? details, CancellationToken cancellationToken)
    {
        try
        {
            await _auditLogService.WriteAsync(action, entityName, entityId, details, cancellationToken);
        }
        catch
        {
            // Audit logging is best-effort and must not block the business write.
        }
    }
}
