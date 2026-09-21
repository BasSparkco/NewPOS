using System.Globalization;
using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Entities;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class SettingsService : ISettingsService
{
    private const string AllowNegativeStockKey = "AllowNegativeStock";
    private const string LowStockThresholdKey = "LowStockThreshold";
    private const string DefaultTaxPercentKey = "DefaultTaxPercent";
    private const string ReceiptFooterTextKey = "ReceiptFooterText";
    private const string UseArabicIndicDigitsKey = "UseArabicIndicDigits";
    private const string PricesIncludeVatKey = "PricesIncludeVat";

    private static readonly StoreSettingsDto DefaultSettings = new(
        AllowNegativeStock: false,
        LowStockThreshold: 5m,
        DefaultTaxPercent: 0m,
        ReceiptFooterText: null,
        UseArabicIndicDigits: false,
        PricesIncludeVat: false);

    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly ICurrentSession _session;

    public SettingsService(IDbContextFactory<PosDbContext> dbFactory, ICurrentSession session)
    {
        _dbFactory = dbFactory;
        _session = session;
    }

    public async Task<StoreSettingsDto> GetStoreSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var entries = await db.Settings
            .AsNoTracking()
            .Where(s => s.StoreId == _session.StoreId && !s.IsDeleted)
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        return new StoreSettingsDto(
            AllowNegativeStock: ParseBool(entries, AllowNegativeStockKey, DefaultSettings.AllowNegativeStock),
            LowStockThreshold: ParseDecimal(entries, LowStockThresholdKey, DefaultSettings.LowStockThreshold),
            DefaultTaxPercent: ParseDecimal(entries, DefaultTaxPercentKey, DefaultSettings.DefaultTaxPercent),
            ReceiptFooterText: ParseString(entries, ReceiptFooterTextKey, DefaultSettings.ReceiptFooterText),
            UseArabicIndicDigits: ParseBool(entries, UseArabicIndicDigitsKey, DefaultSettings.UseArabicIndicDigits),
            PricesIncludeVat: ParseBool(entries, PricesIncludeVatKey, DefaultSettings.PricesIncludeVat));
    }

    public async Task UpdateStoreSettingsAsync(StoreSettingsDto settings, CancellationToken cancellationToken = default)
    {
        if (settings.LowStockThreshold < 0)
            throw new InvalidOperationException("Low-stock threshold cannot be negative.");

        if (settings.DefaultTaxPercent < 0 || settings.DefaultTaxPercent > 100)
            throw new InvalidOperationException("Default tax percent must be between 0 and 100.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var storeId = _session.StoreId;
        var tenantId = await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == storeId && !s.IsDeleted)
            .Select(s => (Guid?)s.TenantId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");
        var keys = new[] { AllowNegativeStockKey, LowStockThresholdKey, DefaultTaxPercentKey, ReceiptFooterTextKey, UseArabicIndicDigitsKey, PricesIncludeVatKey };
        var existing = await db.Settings
            .Where(s => s.StoreId == storeId && keys.Contains(s.Key) && !s.IsDeleted)
            .ToDictionaryAsync(s => s.Key, cancellationToken);

        var now = DateTime.UtcNow;

        Upsert(existing, db, tenantId, storeId, AllowNegativeStockKey, settings.AllowNegativeStock ? "true" : "false", now);
        Upsert(existing, db, tenantId, storeId, LowStockThresholdKey, settings.LowStockThreshold.ToString(CultureInfo.InvariantCulture), now);
        Upsert(existing, db, tenantId, storeId, DefaultTaxPercentKey, settings.DefaultTaxPercent.ToString(CultureInfo.InvariantCulture), now);
        Upsert(existing, db, tenantId, storeId, ReceiptFooterTextKey, (settings.ReceiptFooterText ?? string.Empty).Trim(), now);
        Upsert(existing, db, tenantId, storeId, UseArabicIndicDigitsKey, settings.UseArabicIndicDigits ? "true" : "false", now);
        Upsert(existing, db, tenantId, storeId, PricesIncludeVatKey, settings.PricesIncludeVat ? "true" : "false", now);

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Upsert(
        IReadOnlyDictionary<string, Setting> existing,
        PosDbContext db,
        Guid tenantId,
        Guid storeId,
        string key,
        string value,
        DateTime now)
    {
        if (existing.TryGetValue(key, out var setting))
        {
            setting.Value = value;
            setting.UpdatedAt = now;
            return;
        }

        db.Settings.Add(new Setting
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StoreId = storeId,
            Key = key,
            Value = value,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false
        });
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : fallback;

    private static decimal ParseDecimal(IReadOnlyDictionary<string, string> values, string key, decimal fallback) =>
        values.TryGetValue(key, out var raw) && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static string? ParseString(IReadOnlyDictionary<string, string> values, string key, string? fallback)
    {
        if (!values.TryGetValue(key, out var raw))
            return fallback;

        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
}