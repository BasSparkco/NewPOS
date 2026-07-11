using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Application.Support;
using POS.Core;
using POS.Core.Entities;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class CurrencyService : ICurrencyService
{
    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly IAuditLogService _auditLogService;
    private readonly ICurrentSession _session;

    public CurrencyService(IDbContextFactory<PosDbContext> dbFactory, ICurrentSession session, IAuditLogService auditLogService)
    {
        _dbFactory = dbFactory;
        _auditLogService = auditLogService;
        _session   = session;
    }

    public async Task<IReadOnlyList<CurrencyDto>> GetActiveCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.Currencies
            .AsNoTracking()
            .Where(c => !c.IsDeleted)
            .OrderBy(c => c.Code)
            .Select(c => new CurrencyDto(c.Id, c.Code, c.Name, c.Symbol, c.ExchangeRate))
            .ToListAsync(cancellationToken);
        return list;
    }

    public async Task<StoreCurrencyPolicyDto> GetStoreCurrencyPolicyAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var store = await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == _session.StoreId && !s.IsDeleted)
            .Select(s => new { s.Id, s.Name, s.BaseCurrencyId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");

        var currencies = await db.Currencies
            .AsNoTracking()
            .Where(c => !c.IsDeleted)
            .OrderBy(c => c.Code)
            .Select(c => new CurrencyDto(c.Id, c.Code, c.Name, c.Symbol, c.ExchangeRate))
            .ToListAsync(cancellationToken);

        return new StoreCurrencyPolicyDto(store.Id, store.Name, store.BaseCurrencyId, currencies);
    }

    public async Task<decimal> ConvertToStoreBaseAsync(decimal amount, string fromCurrencyCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fromCurrencyCode))
            throw new ArgumentException("Currency code is required.", nameof(fromCurrencyCode));

        var code = fromCurrencyCode.Trim().ToUpperInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeCurrencyId = await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == _session.StoreId && !s.IsDeleted)
            .Select(s => (Guid?)s.BaseCurrencyId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");

        var baseCur = await db.Currencies
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == storeCurrencyId && !c.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Store has no base currency configured.");

        var from = await db.Currencies
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == code && !c.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown currency: {code}");

        return CurrencyConversion.ToBase(amount, from.ExchangeRate, baseCur.ExchangeRate);
    }

    public async Task UpdateStoreCurrencyPolicyAsync(Guid baseCurrencyId, IReadOnlyList<CurrencyRateUpdateDto> rates,
        CancellationToken cancellationToken = default)
    {
        var normalizedRates = rates
            .GroupBy(r => r.CurrencyId)
            .Select(g => g.Last())
            .ToList();

        if (normalizedRates.All(r => r.CurrencyId != baseCurrencyId))
            normalizedRates.Add(new CurrencyRateUpdateDto(baseCurrencyId, 1m));

        if (normalizedRates.Count == 0)
            throw new InvalidOperationException("At least one currency rate is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var store = await db.Stores
            .FirstOrDefaultAsync(s => s.Id == _session.StoreId && !s.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");

        var currencies = await db.Currencies
            .Where(c => !c.IsDeleted)
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        if (!currencies.TryGetValue(baseCurrencyId, out var newBaseCurrency))
            throw new InvalidOperationException("Selected base currency was not found.");

        var oldBaseCurrency = await db.Currencies
            .FirstOrDefaultAsync(c => c.Id == store.BaseCurrencyId && !c.IsDeleted, cancellationToken);

        if (oldBaseCurrency is null)
        {
            // If the store references a missing base currency, accept the incoming base and re-anchor the policy.
            oldBaseCurrency = newBaseCurrency;
            store.BaseCurrencyId = baseCurrencyId;
        }

        foreach (var rate in normalizedRates)
        {
            if (!currencies.TryGetValue(rate.CurrencyId, out var currency))
                throw new InvalidOperationException("One or more currencies could not be loaded.");

            if (rate.ExchangeRate <= 0)
                throw new InvalidOperationException($"Exchange rate for {currency.Code} must be greater than zero.");
        }

        var now = DateTime.UtcNow;
        var baseChanged = oldBaseCurrency.Id != baseCurrencyId;
        var oldBaseRate = oldBaseCurrency.ExchangeRate;
        var newBaseOldRate = newBaseCurrency.ExchangeRate;
        var oldBaseCode = oldBaseCurrency.Code;

        if (baseChanged)
            await ConvertStoreMonetaryDataAsync(db, oldBaseRate, newBaseOldRate, newBaseCurrency.Code, now, cancellationToken);

        if (!baseChanged)
        {
            foreach (var rate in normalizedRates)
            {
                var targetRate = rate.CurrencyId == baseCurrencyId
                    ? 1m
                    : Math.Round(rate.ExchangeRate, 6, MidpointRounding.AwayFromZero);

                await db.Currencies
                    .Where(c => c.Id == rate.CurrencyId && !c.IsDeleted)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(c => c.ExchangeRate, targetRate)
                        .SetProperty(c => c.UpdatedAt, now), cancellationToken);
            }

            await db.Stores
                .Where(s => s.Id == store.Id && !s.IsDeleted)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(s => s.BaseCurrencyId, baseCurrencyId)
                    .SetProperty(s => s.UpdatedAt, now), cancellationToken);

            db.SyncChanges.Add(new SyncChange
            {
                StoreId = store.Id,
                AggregateType = SyncAggregateTypes.CurrencyPolicy,
                EntityId = store.Id,
                ChangedAt = now
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            foreach (var rate in normalizedRates)
            {
                var currency = currencies[rate.CurrencyId];
                currency.ExchangeRate = currency.Id == baseCurrencyId
                    ? 1m
                    : Math.Round(rate.ExchangeRate, 6, MidpointRounding.AwayFromZero);
                currency.UpdatedAt = now;
            }

            store.BaseCurrencyId = baseCurrencyId;
            store.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken);
        }

        var rateSummary = string.Join(", ",
            normalizedRates.OrderBy(r => currencies[r.CurrencyId].Code)
                .Select(r => $"{currencies[r.CurrencyId].Code}={(r.CurrencyId == baseCurrencyId ? 1m : Math.Round(r.ExchangeRate, 6, MidpointRounding.AwayFromZero)):0.######}"));

        await TryWriteAuditAsync(
            "CurrencyPolicyUpdated",
            "Store",
            store.Id,
            $"Base currency {oldBaseCode} -> {newBaseCurrency.Code}; rates: {rateSummary}",
            cancellationToken);
    }

    private static async Task ConvertStoreMonetaryDataAsync(
        PosDbContext db,
        decimal oldBaseRate,
        decimal newBaseRate,
        string newBaseCode,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var product in db.Products.Where(p => !p.IsDeleted))
        {
            product.Price = ConvertAmount(product.Price, oldBaseRate, newBaseRate);
            product.Cost = ConvertAmount(product.Cost, oldBaseRate, newBaseRate);
            product.UpdatedAt = now;
        }

        var invoices = await db.Invoices
            .Include(i => i.Items)
            .Where(i => !i.IsDeleted && (i.Status == POS.Core.Enums.InvoiceStatus.Open || i.Status == POS.Core.Enums.InvoiceStatus.Held))
            .ToListAsync(cancellationToken);

        foreach (var invoice in invoices)
        {
            foreach (var line in invoice.Items.Where(l => !l.IsDeleted))
            {
                line.UnitPrice = ConvertAmount(line.UnitPrice, oldBaseRate, newBaseRate);
                line.LineTotal = Math.Round(
                    line.Quantity * line.UnitPrice * (1m - line.DiscountPercent / 100m),
                    2,
                    MidpointRounding.AwayFromZero);
                line.UpdatedAt = now;
            }

            var subtotal = invoice.Items.Where(l => !l.IsDeleted).Sum(l => l.LineTotal);
            var taxAmount = Math.Round(subtotal * invoice.TaxPercent / 100m, 2, MidpointRounding.AwayFromZero);
            invoice.TotalAmount = subtotal + taxAmount;
            invoice.Currency = newBaseCode;
            invoice.UpdatedAt = now;
        }
    }

    private static decimal ConvertAmount(decimal amount, decimal fromRate, decimal baseRate) =>
        Math.Round(CurrencyConversion.ToBase(amount, fromRate, baseRate), 2, MidpointRounding.AwayFromZero);

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
