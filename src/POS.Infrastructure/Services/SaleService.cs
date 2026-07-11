using System.Globalization;
using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class SaleService : ISaleService
{
    private const string AllowNegativeStockKey = "AllowNegativeStock";
    private const string DefaultTaxPercentKey = "DefaultTaxPercent";
    private const string CustomItemCategoryName = "Custom Items";
    private const decimal CustomItemStockBuffer = 1_000_000m;

    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly IAuditLogService _auditLogService;
    private readonly ICurrentDevice _currentDevice;
    private readonly ICurrentSession _session;

    public SaleService(
        IDbContextFactory<PosDbContext> dbFactory,
        ICurrentSession session,
        IAuditLogService auditLogService,
        ICurrentDevice currentDevice)
    {
        _dbFactory = dbFactory;
        _auditLogService = auditLogService;
        _currentDevice = currentDevice;
        _session = session;
    }

    public async Task<Guid> StartNewSaleAsync(CancellationToken cancellationToken = default)
    {
        var userId  = _session.UserId;
        var storeId = _session.StoreId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOperationalSettingsAsync(db, cancellationToken);

        var now = DateTime.UtcNow;
        var device = await GetOrCreateCurrentDeviceAsync(db, storeId, now, cancellationToken);
        var invoice = new Invoice
        {
            Id          = Guid.NewGuid(),
            StoreId     = storeId,
            DeviceId    = device.Id,
            UserId      = userId,
            Status      = InvoiceStatus.Open,
            TotalAmount = 0,
            TaxPercent  = settings.DefaultTaxPercent,
            Currency    = _session.BaseCurrencyCode,
            IsSynced    = false,
            SyncVersion = 1,
            CreatedAt   = now,
            UpdatedAt   = now
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(cancellationToken);
        return invoice.Id;
    }

    public async Task CancelInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await db.Invoices
            .FirstOrDefaultAsync(
                i => i.Id == invoiceId
                     && (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.Held)
                     && !i.IsDeleted,
                cancellationToken);
        if (invoice is null) return;
            invoice.Status = InvoiceStatus.Cancelled;
            MarkInvoicePendingSync(invoice);
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "InvoiceCancelled",
            "Invoice",
            invoice.Id,
            $"Cancelled invoice {FormatInvoiceRef(invoice.Id)}.",
            cancellationToken);
    }

    public async Task HoldInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await db.Invoices
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.Status == InvoiceStatus.Open && !i.IsDeleted,
                cancellationToken);
        if (invoice is null) return;
            invoice.Status = InvoiceStatus.Held;
            MarkInvoicePendingSync(invoice);
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "InvoiceHeld",
            "Invoice",
            invoice.Id,
            $"Placed invoice {FormatInvoiceRef(invoice.Id)} on hold.",
            cancellationToken);
    }

    public async Task ResumeInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await db.Invoices
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.Status == InvoiceStatus.Held && !i.IsDeleted,
                cancellationToken);
        if (invoice is null) return;
            invoice.Status = InvoiceStatus.Open;
            MarkInvoicePendingSync(invoice);
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "InvoiceResumed",
            "Invoice",
            invoice.Id,
            $"Resumed invoice {FormatInvoiceRef(invoice.Id)}.",
            cancellationToken);
    }

    public async Task AddOrMergeLineAsync(Guid invoiceId, Guid productId, decimal quantity, CancellationToken cancellationToken = default)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        var storeId = _session.StoreId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var invoice = await db.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status == InvoiceStatus.Held)
            throw new InvalidOperationException("Invoice is on hold. Resume it first.");
        if (invoice.Status != InvoiceStatus.Open)
            throw new InvalidOperationException("Invoice is not open.");

        if (invoice.UserId != _session.UserId)
            throw new InvalidOperationException("Invoice belongs to another user.");

        var product = await db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted && p.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Product not found.");

        var inventory = await db.Inventories
            .FirstOrDefaultAsync(i => i.ProductId == productId && i.StoreId == storeId && !i.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("No inventory row for this product at this store.");

        var settings = await GetOperationalSettingsAsync(db, cancellationToken);

        var existing = invoice.Items.FirstOrDefault(l => l.ProductId == productId && !l.IsDeleted);
        var newQty = (existing?.Quantity ?? 0) + quantity;
        if (!settings.AllowNegativeStock && newQty > inventory.Quantity)
            throw new InvalidOperationException("Insufficient stock.");

        var now = DateTime.UtcNow;
        var unitPrice = product.Price;
        if (existing is null)
        {
            var line = new InvoiceItem
            {
                Id              = Guid.NewGuid(),
                InvoiceId       = invoice.Id,
                ProductId       = productId,
                Quantity        = quantity,
                UnitPrice       = unitPrice,
                DiscountPercent = 0m,
                LineTotal       = CalcLineTotal(quantity, unitPrice, 0m),
                CreatedAt       = now,
                UpdatedAt       = now
            };
            db.InvoiceItems.Add(line);
        }
        else
        {
            existing.Quantity  = newQty;
            existing.UnitPrice = unitPrice;
            existing.LineTotal = CalcLineTotal(newQty, unitPrice, existing.DiscountPercent);
            existing.UpdatedAt = now;
        }

        RecalculateInvoiceTotal(invoice);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Adds a manually-priced, non-catalog line to the invoice. A hidden (IsActive = false) product is
    /// created behind the scenes to carry the name/price so the line flows through the exact same
    /// inventory, receipt, report and refund logic as any catalog sale — nothing else has to special-case it.
    /// </summary>
    public async Task AddCustomItemAsync(Guid invoiceId, string name, decimal quantity, decimal unitPrice, CancellationToken cancellationToken = default)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        if (unitPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(unitPrice));

        var storeId = _session.StoreId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var invoice = await db.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status == InvoiceStatus.Held)
            throw new InvalidOperationException("Invoice is on hold. Resume it first.");
        if (invoice.Status != InvoiceStatus.Open)
            throw new InvalidOperationException("Invoice is not open.");
        if (invoice.UserId != _session.UserId)
            throw new InvalidOperationException("Invoice belongs to another user.");

        var now = DateTime.UtcNow;
        var categoryId = await GetOrCreateCustomItemCategoryIdAsync(db, cancellationToken);

        var product = new Product
        {
            Id         = Guid.NewGuid(),
            Name       = string.IsNullOrWhiteSpace(name) ? "Custom Item" : name.Trim(),
            Price      = unitPrice,
            Cost       = 0m,
            CategoryId = categoryId,
            IsWeighted = false,
            IsActive   = false, // hidden: exists only to back this one-off line, never shown in catalog search
            CreatedAt  = now,
            UpdatedAt  = now
        };
        db.Products.Add(product);

        db.Inventories.Add(new Inventory
        {
            Id         = Guid.NewGuid(),
            ProductId  = product.Id,
            StoreId    = storeId,
            Quantity   = CustomItemStockBuffer,
            CreatedAt  = now,
            UpdatedAt  = now
        });

        var line = new InvoiceItem
        {
            Id              = Guid.NewGuid(),
            InvoiceId       = invoice.Id,
            ProductId       = product.Id,
            Quantity        = quantity,
            UnitPrice       = unitPrice,
            DiscountPercent = 0m,
            LineTotal       = CalcLineTotal(quantity, unitPrice, 0m),
            CreatedAt       = now,
            UpdatedAt       = now
        };
        db.InvoiceItems.Add(line);

        RecalculateInvoiceTotal(invoice);
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "CustomItemAdded",
            "Invoice",
            invoice.Id,
            $"Added custom item '{product.Name}' x{quantity:0.##} @ {unitPrice:0.##} to invoice {FormatInvoiceRef(invoice.Id)}.",
            cancellationToken);
    }

    private static async Task<Guid> GetOrCreateCustomItemCategoryIdAsync(PosDbContext db, CancellationToken cancellationToken)
    {
        var existingId = await db.Categories
            .Where(c => !c.IsDeleted && c.Name == CustomItemCategoryName)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingId != Guid.Empty)
            return existingId;

        var now = DateTime.UtcNow;
        var category = new Category { Id = Guid.NewGuid(), Name = CustomItemCategoryName, CreatedAt = now, UpdatedAt = now };
        db.Categories.Add(category);
        return category.Id;
    }

    public async Task SetLineQuantityAsync(Guid invoiceId, Guid lineId, decimal quantity, CancellationToken cancellationToken = default)
    {
        if (quantity < 0) quantity = 0;
        var storeId = _session.StoreId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var invoice = await db.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status != InvoiceStatus.Open)
            throw new InvalidOperationException("Invoice is not open.");

        if (invoice.UserId != _session.UserId)
            throw new InvalidOperationException("Invoice belongs to another user.");

        var line = invoice.Items.FirstOrDefault(l => l.Id == lineId && !l.IsDeleted)
            ?? throw new InvalidOperationException("Line not found.");

        if (quantity == 0)
        {
            line.IsDeleted = true;
            line.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            var inventory = await db.Inventories
                .FirstOrDefaultAsync(i => i.ProductId == line.ProductId && i.StoreId == storeId && !i.IsDeleted, cancellationToken);

            var settings = await GetOperationalSettingsAsync(db, cancellationToken);

            if (!settings.AllowNegativeStock && inventory is not null && quantity > inventory.Quantity)
                throw new InvalidOperationException($"Only {inventory.Quantity:N2} units in stock.");

            line.Quantity  = quantity;
            line.LineTotal = CalcLineTotal(quantity, line.UnitPrice, line.DiscountPercent);
            line.UpdatedAt = DateTime.UtcNow;
        }

        RecalculateInvoiceTotal(invoice);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveLineAsync(Guid invoiceId, Guid lineId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await db.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status != InvoiceStatus.Open)
            throw new InvalidOperationException("Invoice is not open.");

        if (invoice.UserId != _session.UserId)
            throw new InvalidOperationException("Invoice belongs to another user.");

        var line = invoice.Items.FirstOrDefault(l => l.Id == lineId && !l.IsDeleted)
            ?? throw new InvalidOperationException("Line not found.");

        line.IsDeleted = true;
        line.UpdatedAt = DateTime.UtcNow;
        RecalculateInvoiceTotal(invoice);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<decimal> GetInvoiceTotalAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        return invoice.TotalAmount;
    }

    public async Task<IReadOnlyList<CartLineDto>> GetCartLinesAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var lines = await db.InvoiceItems
            .AsNoTracking()
            .Where(l => l.InvoiceId == invoiceId && !l.IsDeleted)
            .Join(db.Products.AsNoTracking(),
                l => l.ProductId,
                p => p.Id,
                (l, p) => new { l, p })
            .OrderBy(x => x.p.Name)
            .Select(x => new CartLineDto(
                x.l.Id, x.l.ProductId, x.p.Name,
                x.l.Quantity, x.l.UnitPrice, x.l.DiscountPercent, x.l.LineTotal))
            .ToListAsync(cancellationToken);
        return lines;
    }

    public async Task SetLineDiscountAsync(Guid invoiceId, Guid lineId, decimal discountPercent, CancellationToken cancellationToken = default)
    {
        discountPercent = Math.Clamp(discountPercent, 0m, 100m);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var invoice = await db.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status != InvoiceStatus.Open)
            throw new InvalidOperationException("Invoice is not open.");

        var line = invoice.Items.FirstOrDefault(l => l.Id == lineId && !l.IsDeleted)
            ?? throw new InvalidOperationException("Line not found.");

        line.DiscountPercent = discountPercent;
        line.LineTotal = CalcLineTotal(line.Quantity, line.UnitPrice, discountPercent);
        line.UpdatedAt = DateTime.UtcNow;

        RecalculateInvoiceTotal(invoice);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetInvoiceTaxAsync(Guid invoiceId, decimal taxPercent, CancellationToken cancellationToken = default)
    {
        taxPercent = Math.Clamp(taxPercent, 0m, 100m);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var invoice = await db.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status != InvoiceStatus.Open)
            throw new InvalidOperationException("Invoice is not open.");

        invoice.TaxPercent = taxPercent;
        RecalculateInvoiceTotal(invoice);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvoiceSummaryDto> GetInvoiceSummaryAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await db.Invoices
            .Include(i => i.Items)
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        var subtotal  = invoice.Items.Where(l => !l.IsDeleted).Sum(l => l.LineTotal);
        var taxAmount = Math.Round(subtotal * invoice.TaxPercent / 100m, 2, MidpointRounding.AwayFromZero);
        return new InvoiceSummaryDto(subtotal, invoice.TaxPercent, taxAmount, subtotal + taxAmount);
    }

    public async Task<SaleCompletionResult> CompleteCashSaleAsync(Guid invoiceId, decimal cashTendered, CancellationToken cancellationToken = default)
    {
        if (cashTendered < 0)
            return new SaleCompletionResult(false, "Cash amount cannot be negative.", null);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var invoice = await db.Invoices
            .Include(i => i.Items)
            .Include(i => i.Store)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken);

        if (invoice is null)
            return new SaleCompletionResult(false, "Invoice not found.", null);

        if (invoice.Status != InvoiceStatus.Open)
            return new SaleCompletionResult(false, "Invoice is not open.", null);

        if (invoice.UserId != _session.UserId)
            return new SaleCompletionResult(false, "Invoice belongs to another user.", null);

        var lines = invoice.Items.Where(l => !l.IsDeleted).ToList();
        if (lines.Count == 0)
            return new SaleCompletionResult(false, "Cart is empty.", null);

        var total = lines.Sum(l => l.LineTotal);
        if (cashTendered + 0.001m < total)
            return new SaleCompletionResult(false, "Cash tendered is less than the total.", null);

        var storeId = invoice.StoreId;
        var now = DateTime.UtcNow;
        var settings = await GetOperationalSettingsAsync(db, cancellationToken);

        foreach (var line in lines)
        {
            var inv = await db.Inventories
                .FirstOrDefaultAsync(i => i.ProductId == line.ProductId && i.StoreId == storeId && !i.IsDeleted, cancellationToken);

            if (inv is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return new SaleCompletionResult(false, "Inventory missing for a line item.", null);
            }

            if (!settings.AllowNegativeStock && inv.Quantity < line.Quantity)
            {
                await tx.RollbackAsync(cancellationToken);
                return new SaleCompletionResult(false, "Insufficient stock for sale.", null);
            }

            inv.Quantity -= line.Quantity;
            inv.UpdatedAt = now;

            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                ProductId = line.ProductId,
                StoreId = storeId,
                InventoryId = inv.Id,
                InvoiceId = invoice.Id,
                InvoiceItemId = line.Id,
                UserId = invoice.UserId,
                Type = StockMovementType.Sale,
                QuantityDelta = -line.Quantity,
                QuantityAfter = inv.Quantity,
                Reference = invoice.Id.ToString("N")[..12].ToUpperInvariant(),
                Notes = "Inventory reduced when completing cash sale.",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var change = cashTendered - total;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            Amount = total,
            Method = PaymentMethod.Cash,
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Payments.Add(payment);

        invoice.TotalAmount = total;
        invoice.Status = InvoiceStatus.Paid;
    MarkInvoicePendingSync(invoice, now);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await TryWriteAuditAsync(
            "SaleCompleted",
            "Invoice",
            invoice.Id,
            $"Completed cash sale {FormatInvoiceRef(invoice.Id)} with {lines.Count} line(s), total {total:0.##}, cash {cashTendered:0.##}, change {change:0.##}.",
            cancellationToken);

        var productNames = await db.Products.AsNoTracking()
            .Where(p => lines.Select(l => l.ProductId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        var receiptLines = lines.Select(l => new ReceiptLineDto(
            productNames.GetValueOrDefault(l.ProductId, "?"),
            l.Quantity,
            l.UnitPrice,
            l.LineTotal)).ToList();

        var receipt = new ReceiptDto
        {
            StoreName = invoice.Store?.Name ?? "",
            InvoiceNumber = invoice.Id.ToString("N")[..12].ToUpperInvariant(),
            PaidAt = now,
            Currency = invoice.Currency,
            Total = total,
            CashTendered = cashTendered,
            Change = change,
            Lines = receiptLines
        };

        return new SaleCompletionResult(true, null, receipt);
    }

    public async Task<(bool Success, string? Error)> RefundInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var invoice = await db.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken);

        if (invoice is null)
            return (false, "Invoice not found.");

        if (invoice.Status != InvoiceStatus.Paid)
            return (false, "Only paid invoices can be refunded.");

        var lines = invoice.Items.Where(l => !l.IsDeleted).ToList();
        var now = DateTime.UtcNow;

        foreach (var line in lines)
        {
            var inv = await db.Inventories
                .FirstOrDefaultAsync(i => i.ProductId == line.ProductId && i.StoreId == invoice.StoreId && !i.IsDeleted,
                    cancellationToken);
            if (inv is not null)
            {
                inv.Quantity  += line.Quantity;   // return stock
                inv.UpdatedAt  = now;

                db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    ProductId = line.ProductId,
                    StoreId = invoice.StoreId,
                    InventoryId = inv.Id,
                    InvoiceId = invoice.Id,
                    InvoiceItemId = line.Id,
                    UserId = invoice.UserId,
                    Type = StockMovementType.Refund,
                    QuantityDelta = line.Quantity,
                    QuantityAfter = inv.Quantity,
                    Reference = invoice.Id.ToString("N")[..12].ToUpperInvariant(),
                    Notes = "Inventory returned on invoice refund.",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        invoice.Status = InvoiceStatus.Cancelled;
        MarkInvoicePendingSync(invoice, now);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await TryWriteAuditAsync(
            "InvoiceRefunded",
            "Invoice",
            invoice.Id,
            $"Refunded invoice {FormatInvoiceRef(invoice.Id)} and restored {lines.Count} line(s) to stock.",
            cancellationToken);

        return (true, null);
    }

    // Calculates total from the already-loaded in-memory Items collection.
    // Must NOT query the DB here — unsaved new items would be missing from the DB at this point.
    private static void RecalculateInvoiceTotal(Invoice invoice)
    {
        var subtotal  = invoice.Items.Where(l => !l.IsDeleted).Sum(l => l.LineTotal);
        var taxAmount = Math.Round(subtotal * invoice.TaxPercent / 100m, 2, MidpointRounding.AwayFromZero);
        invoice.TotalAmount = subtotal + taxAmount;
        MarkInvoicePendingSync(invoice);
    }

    private static void MarkInvoicePendingSync(Invoice invoice, DateTime? now = null)
    {
        invoice.IsSynced = false;
        invoice.SyncVersion = invoice.SyncVersion <= 0 ? 1 : invoice.SyncVersion + 1;
        invoice.UpdatedAt = now ?? DateTime.UtcNow;
    }

    private static decimal CalcLineTotal(decimal qty, decimal unitPrice, decimal discountPercent)
        => Math.Round(qty * unitPrice * (1m - discountPercent / 100m), 2, MidpointRounding.AwayFromZero);

    private static string FormatInvoiceRef(Guid invoiceId) => invoiceId.ToString("N")[..12].ToUpperInvariant();

    private async Task<(bool AllowNegativeStock, decimal DefaultTaxPercent)> GetOperationalSettingsAsync(
        PosDbContext db,
        CancellationToken cancellationToken)
    {
        var values = await db.Settings
            .AsNoTracking()
            .Where(s => s.StoreId == _session.StoreId
                     && (s.Key == AllowNegativeStockKey || s.Key == DefaultTaxPercentKey)
                     && !s.IsDeleted)
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        var allowNegativeStock = values.TryGetValue(AllowNegativeStockKey, out var allowRaw)
            && bool.TryParse(allowRaw, out var allowParsed)
            && allowParsed;

        var defaultTaxPercent = values.TryGetValue(DefaultTaxPercentKey, out var taxRaw)
            && decimal.TryParse(taxRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var taxParsed)
                ? Math.Clamp(taxParsed, 0m, 100m)
                : 0m;

        return (allowNegativeStock, defaultTaxPercent);
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

    private async Task<Device> GetOrCreateCurrentDeviceAsync(
        PosDbContext db,
        Guid storeId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var deviceName = string.IsNullOrWhiteSpace(_currentDevice.Name)
            ? "Unknown Device"
            : _currentDevice.Name.Trim();

        var existing = await db.Devices
            .FirstOrDefaultAsync(d => d.StoreId == storeId && d.Name == deviceName && !d.IsDeleted, cancellationToken);

        if (existing is not null)
            return existing;

        var created = new Device
        {
            Id = Guid.NewGuid(),
            StoreId = storeId,
            Name = deviceName,
            SyncVersion = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Devices.Add(created);
        return created;
    }
}
