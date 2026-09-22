using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    private const string PricesIncludeVatKey = "PricesIncludeVat";
    private const string CustomItemCategoryName = "Custom Items";
    private const decimal CustomItemStockBuffer = 1_000_000m;

    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly IAuditLogService _auditLogService;
    private readonly ICurrentDevice _currentDevice;
    private readonly ICurrentSession _session;
    private readonly IConfiguration _configuration;
    private readonly ICashMovementWriter _cashMovementWriter;

    public SaleService(
        IDbContextFactory<PosDbContext> dbFactory,
        ICurrentSession session,
        IAuditLogService auditLogService,
        ICurrentDevice currentDevice,
        IConfiguration configuration,
        ICashMovementWriter cashMovementWriter)
    {
        _dbFactory = dbFactory;
        _auditLogService = auditLogService;
        _currentDevice = currentDevice;
        _session = session;
        _configuration = configuration;
        _cashMovementWriter = cashMovementWriter;
    }

    public async Task<Guid> StartNewSaleAsync(CancellationToken cancellationToken = default)
    {
        var userId  = _session.UserId;
        var storeId = _session.StoreId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOperationalSettingsAsync(db, cancellationToken);

        var now = DateTime.UtcNow;
        var tenantId = await GetTenantIdAsync(db, storeId, cancellationToken);
        var device = await GetOrCreateCurrentDeviceAsync(db, tenantId, storeId, now, cancellationToken);
        var invoice = new Invoice
        {
            Id          = Guid.NewGuid(),
            TenantId    = tenantId,
            StoreId     = storeId,
            DeviceId    = device.Id,
            RegisterId  = device.RegisterId,
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

        RecalculateInvoiceTotal(invoice, settings.PricesIncludeVat);
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
        var tenantId = await GetTenantIdAsync(db, storeId, cancellationToken);
        var categoryId = await GetOrCreateCustomItemCategoryIdAsync(db, tenantId, cancellationToken);

        var product = new Product
        {
            Id         = Guid.NewGuid(),
            TenantId   = tenantId,
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
            TenantId   = tenantId,
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

        var settings = await GetOperationalSettingsAsync(db, cancellationToken);
        RecalculateInvoiceTotal(invoice, settings.PricesIncludeVat);
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "CustomItemAdded",
            "Invoice",
            invoice.Id,
            $"Added custom item '{product.Name}' x{quantity:0.##} @ {unitPrice:0.##} to invoice {FormatInvoiceRef(invoice.Id)}.",
            cancellationToken);
    }

    private static async Task<Guid> GetOrCreateCustomItemCategoryIdAsync(PosDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var existingId = await db.Categories
            .Where(c => c.TenantId == tenantId && !c.IsDeleted && c.Name == CustomItemCategoryName)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingId != Guid.Empty)
            return existingId;

        var now = DateTime.UtcNow;
        var category = new Category { Id = Guid.NewGuid(), TenantId = tenantId, Name = CustomItemCategoryName, CreatedAt = now, UpdatedAt = now };
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

        var settings = await GetOperationalSettingsAsync(db, cancellationToken);

        if (quantity == 0)
        {
            line.IsDeleted = true;
            line.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            var inventory = await db.Inventories
                .FirstOrDefaultAsync(i => i.ProductId == line.ProductId && i.StoreId == storeId && !i.IsDeleted, cancellationToken);

            if (!settings.AllowNegativeStock && inventory is not null && quantity > inventory.Quantity)
                throw new InvalidOperationException($"Only {inventory.Quantity:N2} units in stock.");

            line.Quantity  = quantity;
            line.LineTotal = CalcLineTotal(quantity, line.UnitPrice, line.DiscountPercent);
            line.UpdatedAt = DateTime.UtcNow;
        }

        RecalculateInvoiceTotal(invoice, settings.PricesIncludeVat);
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
        var settings = await GetOperationalSettingsAsync(db, cancellationToken);
        RecalculateInvoiceTotal(invoice, settings.PricesIncludeVat);
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
                x.l.Quantity, x.l.UnitPrice, x.l.DiscountPercent, x.l.LineTotal,
                x.p.ImagePath))
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

        var settings = await GetOperationalSettingsAsync(db, cancellationToken);
        RecalculateInvoiceTotal(invoice, settings.PricesIncludeVat);
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
        var settings = await GetOperationalSettingsAsync(db, cancellationToken);
        RecalculateInvoiceTotal(invoice, settings.PricesIncludeVat);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetInvoiceNoteAsync(Guid invoiceId, string? note, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var invoice = await db.Invoices
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (invoice.Status != InvoiceStatus.Open)
            throw new InvalidOperationException("Invoice is not open.");

        invoice.Notes = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        invoice.UpdatedAt = DateTime.UtcNow;
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

        var settings = await GetOperationalSettingsAsync(db, cancellationToken);
        var subtotal = invoice.Items.Where(l => !l.IsDeleted).Sum(l => l.LineTotal);
        var (taxAmount, total) = CalcTaxAndTotal(subtotal, invoice.TaxPercent, settings.PricesIncludeVat);
        return new InvoiceSummaryDto(subtotal, invoice.TaxPercent, taxAmount, total, settings.PricesIncludeVat, invoice.Notes);
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

        var settings = await GetOperationalSettingsAsync(db, cancellationToken);
        var subtotal = lines.Sum(l => l.LineTotal);
        var (_, total) = CalcTaxAndTotal(subtotal, invoice.TaxPercent, settings.PricesIncludeVat);
        if (cashTendered + 0.001m < total)
            return new SaleCompletionResult(false, "Cash tendered is less than the total.", null);

        var storeId = invoice.StoreId;
        var now = DateTime.UtcNow;

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
                TenantId = invoice.TenantId,
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

        // Writes the cash-drawer ledger entry into this same db/transaction — it commits or rolls back
        // together with the sale, so a completed cash sale can never end up with a missing movement. A
        // cash sale requires an open, authorized session on this register (tenant.md §5b) — no session
        // means this fails outright rather than silently completing without a movement.
        var (cashSuccess, cashError) = await _cashMovementWriter.WriteSalePaymentAsync(db, invoice, payment, cancellationToken);
        if (!cashSuccess)
        {
            await tx.RollbackAsync(cancellationToken);
            return new SaleCompletionResult(false, cashError, null);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The cash session was closed by someone else between our check above and this commit.
            // Roll back the whole sale (nothing was persisted) rather than let it land with no movement.
            await tx.RollbackAsync(cancellationToken);
            return new SaleCompletionResult(false, "The cash session was closed while completing this sale. Reopen a session and try again.", null);
        }

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
            Lines = receiptLines,
            Notes = invoice.Notes
        };

        return new SaleCompletionResult(true, null, receipt);
    }

    public async Task<(bool Success, string? Error)> RefundInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var invoice = await db.Invoices
            .Include(i => i.Items)
            .Include(i => i.Payments)
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
                    TenantId = invoice.TenantId,
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

        // Same-transaction ledger write as the completed-sale path above — a refund can never post its
        // inventory/status change while silently dropping the offsetting cash-drawer entry. A cash
        // refund requires an open, authorized session on this register, same as completing a cash sale;
        // a non-cash original payment is a no-op here (nothing to record on the drawer).
        var originalPayment = invoice.Payments.Where(p => !p.IsDeleted).OrderByDescending(p => p.PaidAt).FirstOrDefault();
        if (originalPayment is not null)
        {
            var (cashSuccess, cashError) = await _cashMovementWriter.WriteRefundPaymentAsync(db, invoice, originalPayment, cancellationToken);
            if (!cashSuccess)
            {
                await tx.RollbackAsync(cancellationToken);
                return (false, cashError);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return (false, "The cash session was closed while completing this refund. Reopen a session and try again.");
        }

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
    private static void RecalculateInvoiceTotal(Invoice invoice, bool pricesIncludeVat)
    {
        var subtotal = invoice.Items.Where(l => !l.IsDeleted).Sum(l => l.LineTotal);
        var (_, total) = CalcTaxAndTotal(subtotal, invoice.TaxPercent, pricesIncludeVat);
        invoice.TotalAmount = total;
        MarkInvoicePendingSync(invoice);
    }

    /// <summary>
    /// Computes the VAT amount and grand total for a subtotal. When prices already include VAT,
    /// the returned tax amount is the VAT component embedded in the subtotal (informational only)
    /// and is not added on top — the total equals the subtotal.
    /// </summary>
    private static (decimal TaxAmount, decimal Total) CalcTaxAndTotal(decimal subtotal, decimal taxPercent, bool pricesIncludeVat)
    {
        if (pricesIncludeVat)
        {
            var embeddedTax = Math.Round(subtotal - subtotal / (1m + taxPercent / 100m), 2, MidpointRounding.AwayFromZero);
            return (embeddedTax, subtotal);
        }

        var taxAmount = Math.Round(subtotal * taxPercent / 100m, 2, MidpointRounding.AwayFromZero);
        return (taxAmount, subtotal + taxAmount);
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

    private async Task<(bool AllowNegativeStock, decimal DefaultTaxPercent, bool PricesIncludeVat)> GetOperationalSettingsAsync(
        PosDbContext db,
        CancellationToken cancellationToken)
    {
        var values = await db.Settings
            .AsNoTracking()
            .Where(s => s.StoreId == _session.StoreId
                     && (s.Key == AllowNegativeStockKey || s.Key == DefaultTaxPercentKey || s.Key == PricesIncludeVatKey)
                     && !s.IsDeleted)
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        var allowNegativeStock = values.TryGetValue(AllowNegativeStockKey, out var allowRaw)
            && bool.TryParse(allowRaw, out var allowParsed)
            && allowParsed;

        var defaultTaxPercent = values.TryGetValue(DefaultTaxPercentKey, out var taxRaw)
            && decimal.TryParse(taxRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var taxParsed)
                ? Math.Clamp(taxParsed, 0m, 100m)
                : 0m;

        var pricesIncludeVat = values.TryGetValue(PricesIncludeVatKey, out var vatRaw)
            && bool.TryParse(vatRaw, out var vatParsed)
            && vatParsed;

        return (allowNegativeStock, defaultTaxPercent, pricesIncludeVat);
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
        Guid tenantId,
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
        {
            if (existing.IsRevoked)
                throw new DeviceNotAuthorizedException($"This Box ('{deviceName}') has been revoked. Contact an administrator.");

            var requiresEnrollment = _configuration.GetValue<bool>("Sync:RequireDeviceEnrollment");
            if (requiresEnrollment && existing.EnrolledAt is null)
                throw new DeviceNotAuthorizedException($"This Box ('{deviceName}') is provisioned but not yet enrolled. Enter the enrollment code from the Devices screen.");

            return existing;
        }

        if (_configuration.GetValue<bool>("Sync:RequireDeviceEnrollment"))
            throw new DeviceNotAuthorizedException($"This machine ('{deviceName}') is not a recognized Box. Ask an administrator to provision it first.");

        // Lenient default (API/Web hosts, or WPF with enrollment disabled): auto-create and treat as
        // already-trusted, matching pre-enrollment behavior. Also mints this device's own Register so
        // it has a stable number/history from its very first sale (tenant.md Stage 4T cash-session work).
        var nextNumber = (await db.Registers
            .Where(r => r.StoreId == storeId)
            .Select(r => (int?)r.Number)
            .MaxAsync(cancellationToken) ?? 0) + 1;

        var register = new Register
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StoreId = storeId,
            Number = nextNumber,
            Name = deviceName,
            IsActive = true,
            SyncVersion = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Registers.Add(register);

        var created = new Device
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StoreId = storeId,
            RegisterId = register.Id,
            Name = deviceName,
            SyncVersion = 1,
            EnrolledAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Devices.Add(created);
        return created;
    }

    private static async Task<Guid> GetTenantIdAsync(PosDbContext db, Guid storeId, CancellationToken cancellationToken) =>
        await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == storeId && !s.IsDeleted)
            .Select(s => (Guid?)s.TenantId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");
}
