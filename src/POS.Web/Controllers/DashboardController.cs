using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Core.Enums;
using POS.Infrastructure.Data;
using POS.Web.Infrastructure;
using POS.Web.Models;

namespace POS.Web.Controllers;

[Authorize(Policy = WebAuthorizationPolicies.DashboardAccess)]
public sealed class DashboardController : Controller
{
    private sealed class SalesLineSnapshot
    {
        public string ProductName { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public decimal LineTotal { get; init; }
    }

    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly ISettingsService _settingsService;
    private readonly ICurrentSession _session;

    public DashboardController(
        IDbContextFactory<PosDbContext> dbFactory,
        ISettingsService settingsService,
        ICurrentSession session)
    {
        _dbFactory = dbFactory;
        _settingsService = settingsService;
        _session = session;
    }

    [HttpGet]
    public async Task<IActionResult> Index(DateTime? date, CancellationToken cancellationToken)
    {
        var selectedDate = date?.Date ?? DateTime.Today;
        var dayStartUtc = DateTime.SpecifyKind(selectedDate, DateTimeKind.Local).ToUniversalTime();
        var dayEndUtc = dayStartUtc.AddDays(1);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var store = await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == _session.StoreId && !s.IsDeleted)
            .Select(s => new
            {
                s.Name,
                s.Address,
                s.Phone
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (store is null)
            return NotFound();

        var settings = await _settingsService.GetStoreSettingsAsync(cancellationToken);

        var invoices = await db.Invoices
            .AsNoTracking()
            .Where(i => i.StoreId == _session.StoreId
                        && i.Status == InvoiceStatus.Paid
                        && !i.IsDeleted
                        && i.UpdatedAt >= dayStartUtc
                        && i.UpdatedAt < dayEndUtc)
            .OrderByDescending(i => i.UpdatedAt)
            .Select(i => new
            {
                i.Id,
                i.TotalAmount,
                i.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        var invoiceIds = invoices.Select(i => i.Id).ToList();
        List<SalesLineSnapshot> salesLines = [];
        if (invoiceIds.Count > 0)
        {
            salesLines = await db.InvoiceItems
                .AsNoTracking()
                .Where(item => invoiceIds.Contains(item.InvoiceId) && !item.IsDeleted)
                .Join(
                    db.Products.AsNoTracking().Where(product => !product.IsDeleted),
                    item => item.ProductId,
                    product => product.Id,
                    (item, product) => new SalesLineSnapshot
                    {
                        ProductName = product.Name,
                        Quantity = item.Quantity,
                        LineTotal = item.LineTotal
                    })
                .ToListAsync(cancellationToken);
        }

        var inventoryRows = await db.Inventories
            .AsNoTracking()
            .Where(inventory => inventory.StoreId == _session.StoreId && !inventory.IsDeleted)
            .Join(
                db.Products.AsNoTracking().Where(product => product.IsActive && !product.IsDeleted),
                inventory => inventory.ProductId,
                product => product.Id,
                (inventory, product) => new
                {
                    inventory.ProductId,
                    ProductName = product.Name,
                    product.Price,
                    inventory.Quantity,
                    inventory.UpdatedAt
                })
            .ToListAsync(cancellationToken);

        inventoryRows = inventoryRows
            .OrderBy(entry => entry.Quantity)
            .ThenBy(entry => entry.ProductName)
            .ToList();

        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.StoreId == _session.StoreId && !user.IsDeleted)
            .Join(
                db.Roles.AsNoTracking().Where(role => !role.IsDeleted),
                user => user.RoleId,
                role => role.Id,
                (user, role) => new DashboardUserRowViewModel
                {
                    Username = user.Username,
                    RoleName = role.Name,
                    IsActive = user.IsActive,
                    UpdatedAtLocal = user.UpdatedAt.ToLocalTime()
                })
            .OrderByDescending(user => user.IsActive)
            .ThenBy(user => user.Username)
            .ToListAsync(cancellationToken);

        var revenue = invoices.Sum(i => i.TotalAmount);
        var invoiceCount = invoices.Count;
        var itemsSold = salesLines.Sum(line => line.Quantity);
        var lowStockThreshold = settings.LowStockThreshold;
        var lowStockItems = inventoryRows
            .Where(entry => entry.Quantity <= lowStockThreshold)
            .Take(8)
            .Select(entry => new DashboardInventoryRowViewModel
            {
                ProductName = entry.ProductName,
                Quantity = entry.Quantity,
                Threshold = lowStockThreshold,
                Price = entry.Price,
                UpdatedAtLocal = entry.UpdatedAt.ToLocalTime()
            })
            .ToList();

        var topProducts = salesLines
            .GroupBy(line => line.ProductName)
            .Select(group => new DashboardTopProductViewModel
            {
                ProductName = group.Key,
                QuantitySold = group.Sum(item => item.Quantity),
                Revenue = group.Sum(item => item.LineTotal)
            })
            .OrderByDescending(item => item.Revenue)
            .ThenByDescending(item => item.QuantitySold)
            .Take(6)
            .Select((item, index) =>
            {
                item.Rank = index + 1;
                return item;
            })
            .ToList();

        var model = new DashboardViewModel
        {
            SelectedDate = selectedDate,
            StoreName = store.Name,
            StoreAddress = store.Address,
            StorePhone = store.Phone,
            Username = _session.Username,
            RoleName = _session.RoleName,
            CurrencyCode = _session.BaseCurrencyCode,
            CurrencySymbol = _session.CurrencySymbol,
            Summary = new DashboardSummaryViewModel
            {
                Revenue = revenue,
                InvoiceCount = invoiceCount,
                ItemsSold = itemsSold,
                AverageSale = invoiceCount > 0 ? revenue / invoiceCount : 0m,
                ActiveProducts = inventoryRows.Select(entry => entry.ProductId).Distinct().Count(),
                LowStockCount = lowStockItems.Count,
                ActiveUsers = users.Count(user => user.IsActive)
            },
            TopProducts = topProducts,
            RecentInvoices = invoices
                .Take(8)
                .Select(invoice => new DashboardInvoiceRowViewModel
                {
                    InvoiceNumber = invoice.Id.ToString("N")[..12].ToUpperInvariant(),
                    UpdatedAtLocal = invoice.UpdatedAt.ToLocalTime(),
                    TotalAmount = invoice.TotalAmount
                })
                .ToList(),
            LowStockItems = lowStockItems,
            Users = users
        };

        return View(model);
    }
}