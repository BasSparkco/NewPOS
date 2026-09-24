using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using POS.Core;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class InvoiceSyncService : IInvoiceSyncService
{
    private const int DefaultBatchSize = 25;
    private const string AuditLogsPullCursorSettingKey = "Sync.AuditLogsPullSinceVersion";
    private const string AuditLogsPushCursorSettingKey = "Sync.AuditLogsPushSinceVersion";
    private const string CashSessionPullCursorSettingKey = "Sync.CashSessionPullSinceVersion";
    private const string CashSessionPushCursorSettingKey = "Sync.CashSessionPushSinceVersion";
    private const string RegisterPullCursorSettingKey = "Sync.RegisterPullSinceVersion";
    private const string RegisterPushCursorSettingKey = "Sync.RegisterPushSinceVersion";
    private const string CategoryPullCursorSettingKey = "Sync.CategoryPullSinceVersion";
    private const string CategoryPushCursorSettingKey = "Sync.CategoryPushSinceVersion";
    private const string CurrencyPolicyPullCursorSettingKey = "Sync.CurrencyPolicyPullSinceVersion";
    private const string CurrencyPolicyPushCursorSettingKey = "Sync.CurrencyPolicyPushSinceVersion";
    private const string DevicePullCursorSettingKey = "Sync.DevicePullSinceVersion";
    private const string DevicePushCursorSettingKey = "Sync.DevicePushSinceVersion";
    private const string InvoicePullCursorSettingKey = "Sync.InvoicePullSinceVersion";
    private const string ProductPullCursorSettingKey = "Sync.ProductPullSinceVersion";
    private const string SettingsPullCursorSettingKey = "Sync.SettingsPullSinceVersion";
    private const string ProductPushCursorSettingKey = "Sync.ProductPushSinceVersion";
    private const string SettingsPushCursorSettingKey = "Sync.SettingsPushSinceVersion";
    private const string UserPullCursorSettingKey = "Sync.UserPullSinceVersion";
    private const string UserPushCursorSettingKey = "Sync.UserPushSinceVersion";
    private const string EmptyGuidCursor = "0:00000000000000000000000000000000";
    private const string EmptyKeyCursor = "0:";
    private const string EmptySequenceCursor = "0";
    private const string EmptyTicksCursor = "0";
    private readonly IConfiguration _configuration;
    private readonly ICurrencyService _currencyService;
    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private const string DeviceSecretSettingKey = "Sync.DeviceSecret";
    private const string TenantSlugSettingKey = "Sync.TenantSlug";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICurrentSession _session;
    private readonly ICurrentDevice _currentDevice;
    // One sync pass (InvoiceSyncBackgroundService.ExecuteAsync) calls up to 16 push/pull methods against a
    // fresh scoped InvoiceSyncService instance; caching the authorized client for this instance's lifetime
    // turns that into a single /api/auth/login call per pass instead of one per method — both far kinder to
    // the server and compatible with login rate limiting (tenant.md's identity section).
    private HttpClient? _cachedAuthorizedClient;

    public InvoiceSyncService(
        IDbContextFactory<PosDbContext> dbFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ICurrencyService currencyService,
        ICurrentSession session,
        ICurrentDevice currentDevice)
    {
        _dbFactory = dbFactory;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _currencyService = currencyService;
        _session = session;
        _currentDevice = currentDevice;
    }

    /// <summary>
    /// Restores TenantId/StoreId from the local database's single Store row when no interactive user is
    /// signed in, so the background sync worker keeps running under a device credential after logout
    /// (tenant.md §5a — device authentication is not a substitute for employee login, but it must still
    /// let a logged-out terminal keep syncing). A no-op once a scope already exists (interactive login,
    /// or a previous call in this same pass already resolved it) and a genuine no-op on a fresh install
    /// with no Store yet.
    /// </summary>
    public async Task EnsureSyncScopeAsync(CancellationToken cancellationToken = default)
    {
        if (_session.HasSyncScope)
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var store = await db.Stores
            .AsNoTracking()
            .Where(s => !s.IsDeleted)
            .Select(s => new { s.Id, s.TenantId })
            .FirstOrDefaultAsync(cancellationToken);

        if (store is not null)
            _session.SetDeviceSyncScope(store.TenantId, store.Id);
    }

    public async Task<InvoiceSyncRunResultDto> PushUnsyncedInvoicesAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new InvoiceSyncRunResultDto(0, 0, 0, 0);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new InvoiceSyncRunResultDto(0, 0, 0, 0);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoices = await db.Invoices
            .AsNoTracking()
            .Where(i => i.StoreId == _session.StoreId && !i.IsDeleted && !i.IsSynced)
            .OrderBy(i => i.UpdatedAt)
            .Take(batchSize)
            .Select(i => new InvoiceSyncInvoiceDto(
                i.Id,
                i.DeviceId,
                i.Device != null ? i.Device.Name : null,
                i.SyncVersion,
                i.Status,
                i.TotalAmount,
                i.TaxPercent,
                i.Currency,
                i.Notes,
                i.CreatedAt,
                i.UpdatedAt,
                i.Items
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => new InvoiceSyncLineDto(
                        x.Id,
                        x.ProductId,
                        x.Quantity,
                        x.UnitPrice,
                        x.DiscountPercent,
                        x.LineTotal,
                        x.CreatedAt,
                        x.UpdatedAt,
                        x.IsDeleted))
                    .ToList(),
                i.Payments
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => new InvoiceSyncPaymentDto(
                        x.Id,
                        x.Amount,
                        x.Method,
                        x.PaidAt,
                        x.CreatedAt,
                        x.UpdatedAt,
                        x.IsDeleted))
                    .ToList(),
                i.User != null ? i.User.Username : null))
            .ToListAsync(cancellationToken);

        if (invoices.Count == 0)
            return new InvoiceSyncRunResultDto(0, 0, 0, 0);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new InvoiceSyncRunResultDto(invoices.Count, 0, 0, invoices.Count);

        var pushResponse = await client.PostAsJsonAsync("api/sync/invoices/push", new InvoiceSyncBatchDto(invoices), cancellationToken);
        if (!pushResponse.IsSuccessStatusCode)
            return new InvoiceSyncRunResultDto(invoices.Count, 0, 0, invoices.Count);

        var payload = await pushResponse.Content.ReadFromJsonAsync<InvoiceSyncPushResultDto>(cancellationToken: cancellationToken)
            ?? new InvoiceSyncPushResultDto(Array.Empty<InvoiceSyncInvoiceResultDto>());

        var syncedIds = await MarkLocallySyncedAsync(payload.Results, cancellationToken);
        var conflicts = payload.Results.Count(x => string.Equals(x.Status, "Conflict", StringComparison.OrdinalIgnoreCase));
        var failed = payload.Results.Count(x => string.Equals(x.Status, "Failed", StringComparison.OrdinalIgnoreCase));

        return new InvoiceSyncRunResultDto(invoices.Count, syncedIds, conflicts, failed + Math.Max(0, invoices.Count - payload.Results.Count));
    }

    public async Task<AuditLogSyncPushRunResultDto> PushUpdatedAuditLogsAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new AuditLogSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new AuditLogSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, AuditLogsPushCursorSettingKey, EmptySequenceCursor, cancellationToken);
        if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
            return new AuditLogSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var changes = await GetGuidSyncChangesAsync(
            db,
            SyncAggregateTypes.AuditLog,
            sinceSequence,
            batchSize,
            _session.StoreId,
            includeGlobalRows: false,
            cancellationToken);

        if (changes.Count == 0)
            return new AuditLogSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var auditLogIds = changes.Select(change => change.EntityId).ToArray();
        var auditLogs = await db.AuditLogs
            .AsNoTracking()
            .Where(a => a.StoreId == _session.StoreId && !a.IsDeleted && auditLogIds.Contains(a.Id))
            .Select(a => new AuditLogSyncDto(
                a.Id,
                a.User != null ? a.User.Username : null,
                a.Action,
                a.EntityName,
                a.EntityId,
                a.Details,
                a.CreatedAt))
            .ToListAsync(cancellationToken);

        var payload = OrderByGuidSequence(changes, auditLogs, item => item.Id);

        if (payload.Count == 0)
            return new AuditLogSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new AuditLogSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var response = await client.PostAsJsonAsync("api/sync/audit-logs/push", new AuditLogSyncBatchDto(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new AuditLogSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var result = await response.Content.ReadFromJsonAsync<AuditLogSyncPushResultDto>(cancellationToken: cancellationToken)
            ?? new AuditLogSyncPushResultDto(Array.Empty<AuditLogSyncItemResultDto>());

        var nextCursor = ComputeNextGuidSequenceCursor(
            changes,
            result.Results,
            normalizedSinceVersion,
            row => row.AuditLogId,
            row => row.Status);

        if (!string.Equals(nextCursor, normalizedSinceVersion, StringComparison.Ordinal))
        {
            await UpsertCursorAsync(db, AuditLogsPushCursorSettingKey, nextCursor, EmptySequenceCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var sent = result.Results.Count(x => string.Equals(x.Status, "Applied", StringComparison.OrdinalIgnoreCase));
        var skipped = result.Results.Count(x => string.Equals(x.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
        var failed = payload.Count - sent - skipped;

        return new AuditLogSyncPushRunResultDto(payload.Count, sent, skipped, failed, nextCursor);
    }

    public async Task<CategorySyncPushRunResultDto> PushUpdatedCategoriesAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new CategorySyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new CategorySyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, CategoryPushCursorSettingKey, EmptySequenceCursor, cancellationToken);
        if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
            return new CategorySyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var changes = await GetGuidSyncChangesAsync(
            db,
            SyncAggregateTypes.Category,
            sinceSequence,
            batchSize,
            storeId: null,
            includeGlobalRows: false,
            cancellationToken);

        if (changes.Count == 0)
            return new CategorySyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var categoryIds = changes.Select(change => change.EntityId).ToArray();
        var categories = await db.Categories
            .AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id))
            .Select(c => new CategorySyncDto(
                c.Id,
                c.Name,
                c.CreatedAt,
                c.UpdatedAt,
                c.IsDeleted))
            .ToListAsync(cancellationToken);

        var payload = OrderByGuidSequence(changes, categories, item => item.CategoryId);

        if (payload.Count == 0)
            return new CategorySyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new CategorySyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var response = await client.PostAsJsonAsync("api/sync/categories/push", new CategorySyncBatchDto(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new CategorySyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var result = await response.Content.ReadFromJsonAsync<CategorySyncPushResultDto>(cancellationToken: cancellationToken)
            ?? new CategorySyncPushResultDto(Array.Empty<CategorySyncItemResultDto>());

        var nextCursor = ComputeNextGuidSequenceCursor(
            changes,
            result.Results,
            normalizedSinceVersion,
            row => row.CategoryId,
            row => row.Status);

        if (!string.Equals(nextCursor, normalizedSinceVersion, StringComparison.Ordinal))
        {
            await UpsertCursorAsync(db, CategoryPushCursorSettingKey, nextCursor, EmptySequenceCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var sent = result.Results.Count(x => string.Equals(x.Status, "Applied", StringComparison.OrdinalIgnoreCase));
        var skipped = result.Results.Count(x => string.Equals(x.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
        var failed = payload.Count - sent - skipped;

        return new CategorySyncPushRunResultDto(payload.Count, sent, skipped, failed, nextCursor);
    }

    public async Task<CurrencyPolicySyncPushRunResultDto> PushCurrencyPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new CurrencyPolicySyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new CurrencyPolicySyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, CurrencyPolicyPushCursorSettingKey, EmptySequenceCursor, cancellationToken);
        if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
            return new CurrencyPolicySyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var latestSequence = await GetLatestSyncSequenceAsync(
            db,
            SyncAggregateTypes.CurrencyPolicy,
            _session.StoreId,
            includeGlobalRows: true,
            cancellationToken);

        if (latestSequence <= sinceSequence)
            return new CurrencyPolicySyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var currentUpdatedAt = await GetCurrencyPolicyUpdatedAtAsync(db, cancellationToken);

        var policy = await _currencyService.GetStoreCurrencyPolicyAsync(cancellationToken);
        var payload = new CurrencyPolicySyncDto(policy.StoreId, policy.BaseCurrencyId, currentUpdatedAt, policy.Currencies);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new CurrencyPolicySyncPushRunResultDto(1, 0, 0, 1, normalizedSinceVersion);

        var response = await client.PostAsJsonAsync("api/sync/currency-policy/push", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new CurrencyPolicySyncPushRunResultDto(1, 0, 0, 1, normalizedSinceVersion);

        var result = await response.Content.ReadFromJsonAsync<CurrencyPolicySyncPushResultDto>(cancellationToken: cancellationToken)
            ?? new CurrencyPolicySyncPushResultDto("Failed", currentUpdatedAt, "Empty response.");

        if (!string.Equals(result.Status, "Applied", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(result.Status, "Skipped", StringComparison.OrdinalIgnoreCase))
            return new CurrencyPolicySyncPushRunResultDto(1, 0, 0, 1, normalizedSinceVersion);

        var nextCursor = FormatSequenceSinceVersion(latestSequence);
        await UpsertCursorAsync(db, CurrencyPolicyPushCursorSettingKey, nextCursor, EmptySequenceCursor, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return new CurrencyPolicySyncPushRunResultDto(
            1,
            string.Equals(result.Status, "Applied", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            string.Equals(result.Status, "Skipped", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            0,
            nextCursor);
    }

    public async Task<DeviceSyncPushRunResultDto> PushUpdatedDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new DeviceSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new DeviceSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, DevicePushCursorSettingKey, EmptySequenceCursor, cancellationToken);
        if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
            return new DeviceSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var changes = await GetGuidSyncChangesAsync(
            db,
            SyncAggregateTypes.Device,
            sinceSequence,
            batchSize,
            _session.StoreId,
            includeGlobalRows: false,
            cancellationToken);

        if (changes.Count == 0)
            return new DeviceSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var deviceIds = changes.Select(change => change.EntityId).ToArray();
        var devices = await db.Devices
            .AsNoTracking()
            .Where(d => d.StoreId == _session.StoreId && deviceIds.Contains(d.Id))
            .Select(d => new DeviceSyncDto(
                d.Id,
                d.Name,
                d.SyncVersion,
                d.CreatedAt,
                d.UpdatedAt,
                d.IsDeleted,
                d.EnrollmentCodeHash,
                d.EnrolledAt,
                d.IsRevoked,
                d.RevokedAt))
            .ToListAsync(cancellationToken);

        var payload = OrderByGuidSequence(changes, devices, item => item.DeviceId);

        if (payload.Count == 0)
            return new DeviceSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new DeviceSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var response = await client.PostAsJsonAsync("api/sync/devices/push", new DeviceSyncBatchDto(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new DeviceSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var result = await response.Content.ReadFromJsonAsync<DeviceSyncPushResultDto>(cancellationToken: cancellationToken)
            ?? new DeviceSyncPushResultDto(Array.Empty<DeviceSyncItemResultDto>());

        var nextCursor = ComputeNextGuidSequenceCursor(
            changes,
            result.Results,
            normalizedSinceVersion,
            row => row.DeviceId,
            row => row.Status);

        if (!string.Equals(nextCursor, normalizedSinceVersion, StringComparison.Ordinal))
        {
            await UpsertCursorAsync(db, DevicePushCursorSettingKey, nextCursor, EmptySequenceCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var sent = result.Results.Count(x => string.Equals(x.Status, "Applied", StringComparison.OrdinalIgnoreCase));
        var skipped = result.Results.Count(x => string.Equals(x.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
        var failed = payload.Count - sent - skipped;

        return new DeviceSyncPushRunResultDto(payload.Count, sent, skipped, failed, nextCursor);
    }

    public async Task<CategorySyncPullRunResultDto> PullRemoteCategoriesAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new CategorySyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new CategorySyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new CategorySyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, CategoryPullCursorSettingKey, EmptySequenceCursor, cancellationToken);
        var pullResponse = await client.GetAsync(
            $"api/sync/categories/pull?sinceVersion={Uri.EscapeDataString(sinceVersion)}&batchSize={batchSize}",
            cancellationToken);

        if (!pullResponse.IsSuccessStatusCode)
            return new CategorySyncPullRunResultDto(0, 0, 0, 0, sinceVersion);

        var payload = await pullResponse.Content.ReadFromJsonAsync<CategorySyncPullResultDto>(cancellationToken: cancellationToken)
            ?? new CategorySyncPullResultDto(Array.Empty<CategorySyncDto>(), sinceVersion);

        if (payload.Categories.Count == 0)
        {
            if (!string.Equals(payload.NextSinceVersion, sinceVersion, StringComparison.Ordinal))
            {
                await UpsertCursorAsync(db, CategoryPullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new CategorySyncPullRunResultDto(0, 0, 0, 0, payload.NextSinceVersion);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var applied = 0;
            var skipped = 0;
            var tenantId = await GetTenantIdAsync(db, cancellationToken);

            foreach (var incoming in payload.Categories)
            {
                var existing = await db.Categories.FirstOrDefaultAsync(c => c.Id == incoming.CategoryId, cancellationToken);
                if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
                {
                    skipped++;
                    continue;
                }

                if (existing is null)
                {
                    existing = new Category
                    {
                        Id = incoming.CategoryId,
                        TenantId = tenantId
                    };
                    db.Categories.Add(existing);
                }

                ApplyCategorySnapshot(existing, incoming);
                applied++;
            }

            await UpsertCursorAsync(db, CategoryPullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new CategorySyncPullRunResultDto(payload.Categories.Count, applied, skipped, 0, payload.NextSinceVersion);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            return new CategorySyncPullRunResultDto(payload.Categories.Count, 0, 0, payload.Categories.Count, sinceVersion);
        }
    }

    public async Task<CurrencyPolicySyncPullRunResultDto> PullCurrencyPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new CurrencyPolicySyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new CurrencyPolicySyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new CurrencyPolicySyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, CurrencyPolicyPullCursorSettingKey, EmptySequenceCursor, cancellationToken);
        var response = await client.GetAsync($"api/sync/currency-policy/pull?sinceVersion={Uri.EscapeDataString(sinceVersion)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new CurrencyPolicySyncPullRunResultDto(0, 0, 0, 0, sinceVersion);

        var payload = await response.Content.ReadFromJsonAsync<CurrencyPolicySyncPullResultDto>(cancellationToken: cancellationToken)
            ?? new CurrencyPolicySyncPullResultDto(null, sinceVersion);

        if (payload.Policy is null)
        {
            if (!string.Equals(payload.NextSinceVersion, sinceVersion, StringComparison.Ordinal))
            {
                await UpsertCursorAsync(db, CurrencyPolicyPullCursorSettingKey, payload.NextSinceVersion, EmptySequenceCursor, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new CurrencyPolicySyncPullRunResultDto(0, 0, 0, 0, payload.NextSinceVersion);
        }

        var localUpdatedAt = await GetCurrencyPolicyUpdatedAtAsync(db, cancellationToken);
        if (localUpdatedAt >= payload.Policy.UpdatedAt)
        {
            var localPushSequence = await GetLatestSyncSequenceAsync(
                db,
                SyncAggregateTypes.CurrencyPolicy,
                _session.StoreId,
                includeGlobalRows: true,
                cancellationToken);
            await UpsertCursorAsync(db, CurrencyPolicyPullCursorSettingKey, payload.NextSinceVersion, EmptySequenceCursor, cancellationToken);
            await UpsertCursorAsync(db, CurrencyPolicyPushCursorSettingKey, FormatSequenceSinceVersion(localPushSequence), EmptySequenceCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new CurrencyPolicySyncPullRunResultDto(1, 0, 1, 0, payload.NextSinceVersion);
        }

        var rates = payload.Policy.Currencies
            .Select(c => new CurrencyRateUpdateDto(c.Id, c.ExchangeRate))
            .ToList();

        await _currencyService.UpdateStoreCurrencyPolicyAsync(payload.Policy.BaseCurrencyId, rates, cancellationToken);

        await using var afterDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var appliedPushSequence = await GetLatestSyncSequenceAsync(
            afterDb,
            SyncAggregateTypes.CurrencyPolicy,
            _session.StoreId,
            includeGlobalRows: true,
            cancellationToken);
        await UpsertCursorAsync(afterDb, CurrencyPolicyPullCursorSettingKey, payload.NextSinceVersion, EmptySequenceCursor, cancellationToken);
        await UpsertCursorAsync(afterDb, CurrencyPolicyPushCursorSettingKey, FormatSequenceSinceVersion(appliedPushSequence), EmptySequenceCursor, cancellationToken);
        await afterDb.SaveChangesAsync(cancellationToken);

        return new CurrencyPolicySyncPullRunResultDto(1, 1, 0, 0, payload.NextSinceVersion);
    }

    public async Task<AuditLogSyncPullRunResultDto> PullRemoteAuditLogsAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new AuditLogSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new AuditLogSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new AuditLogSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, AuditLogsPullCursorSettingKey, EmptySequenceCursor, cancellationToken);
        var pullResponse = await client.GetAsync(
            $"api/sync/audit-logs/pull?sinceVersion={Uri.EscapeDataString(sinceVersion)}&batchSize={batchSize}",
            cancellationToken);

        if (!pullResponse.IsSuccessStatusCode)
            return new AuditLogSyncPullRunResultDto(0, 0, 0, 0, sinceVersion);

        var payload = await pullResponse.Content.ReadFromJsonAsync<AuditLogSyncPullResultDto>(cancellationToken: cancellationToken)
            ?? new AuditLogSyncPullResultDto(Array.Empty<AuditLogSyncDto>(), sinceVersion);

        if (payload.AuditLogs.Count == 0)
        {
            if (!string.Equals(payload.NextSinceVersion, sinceVersion, StringComparison.Ordinal))
            {
                await UpsertCursorAsync(db, AuditLogsPullCursorSettingKey, payload.NextSinceVersion, EmptyKeyCursor, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new AuditLogSyncPullRunResultDto(0, 0, 0, 0, payload.NextSinceVersion);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var applied = 0;
            var skipped = 0;

            foreach (var incoming in payload.AuditLogs)
            {
                var existing = await db.AuditLogs
                    .FirstOrDefaultAsync(a => a.Id == incoming.Id && a.StoreId == _session.StoreId && !a.IsDeleted, cancellationToken);

                if (existing is not null)
                {
                    skipped++;
                    continue;
                }

                var userId = await ResolveAuditLogUserIdAsync(db, incoming.Username, cancellationToken);
                db.AuditLogs.Add(new AuditLog
                {
                    Id = incoming.Id,
                    TenantId = await GetTenantIdAsync(db, cancellationToken),
                    StoreId = _session.StoreId,
                    UserId = userId,
                    Action = incoming.Action,
                    EntityName = incoming.EntityName,
                    EntityId = incoming.EntityId,
                    Details = incoming.Details,
                    CreatedAt = incoming.CreatedAt,
                    UpdatedAt = incoming.CreatedAt,
                    IsDeleted = false
                });
                applied++;
            }

            await UpsertCursorAsync(db, AuditLogsPullCursorSettingKey, payload.NextSinceVersion, EmptyKeyCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new AuditLogSyncPullRunResultDto(payload.AuditLogs.Count, applied, skipped, 0, payload.NextSinceVersion);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            return new AuditLogSyncPullRunResultDto(payload.AuditLogs.Count, 0, 0, payload.AuditLogs.Count, sinceVersion);
        }
    }

    public async Task<DeviceSyncPullRunResultDto> PullRemoteDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new DeviceSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new DeviceSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new DeviceSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, DevicePullCursorSettingKey, EmptySequenceCursor, cancellationToken);
        var pullResponse = await client.GetAsync(
            $"api/sync/devices/pull?sinceVersion={Uri.EscapeDataString(sinceVersion)}&batchSize={batchSize}",
            cancellationToken);

        if (!pullResponse.IsSuccessStatusCode)
            return new DeviceSyncPullRunResultDto(0, 0, 0, 0, sinceVersion);

        var payload = await pullResponse.Content.ReadFromJsonAsync<DeviceSyncPullResultDto>(cancellationToken: cancellationToken)
            ?? new DeviceSyncPullResultDto(Array.Empty<DeviceSyncDto>(), sinceVersion);

        if (payload.Devices.Count == 0)
        {
            if (!string.Equals(payload.NextSinceVersion, sinceVersion, StringComparison.Ordinal))
            {
                await UpsertCursorAsync(db, DevicePullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new DeviceSyncPullRunResultDto(0, 0, 0, 0, payload.NextSinceVersion);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var applied = 0;
            var skipped = 0;

            foreach (var incoming in payload.Devices)
            {
                var existing = await UpsertDeviceSnapshotAsync(db, _session.StoreId, incoming, cancellationToken);
                if (existing.UpdatedAt > incoming.UpdatedAt)
                {
                    skipped++;
                    continue;
                }

                applied++;
            }

            await UpsertCursorAsync(db, DevicePullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new DeviceSyncPullRunResultDto(payload.Devices.Count, applied, skipped, 0, payload.NextSinceVersion);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            return new DeviceSyncPullRunResultDto(payload.Devices.Count, 0, 0, payload.Devices.Count, sinceVersion);
        }
    }

    /// <summary>
    /// Registers must sync before CashSessions (interface contract) — a CashSession's RegisterId is a
    /// required FK on the server, and a Register created locally by <c>DeviceManagementService.
    /// ProvisionDeviceAsync</c> (a fresh <c>Guid.NewGuid()</c>, not the deterministic backfill id) would
    /// otherwise never reach the server at all, since Register previously had no sync surface of its own.
    /// </summary>
    public async Task<RegisterSyncPushRunResultDto> PushUpdatedRegistersAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new RegisterSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new RegisterSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, RegisterPushCursorSettingKey, EmptySequenceCursor, cancellationToken);
        if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
            return new RegisterSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var changes = await GetGuidSyncChangesAsync(
            db,
            SyncAggregateTypes.Register,
            sinceSequence,
            batchSize,
            _session.StoreId,
            includeGlobalRows: false,
            cancellationToken);

        if (changes.Count == 0)
            return new RegisterSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var registerIds = changes.Select(change => change.EntityId).ToArray();
        var registers = await db.Registers
            .AsNoTracking()
            .Where(r => r.StoreId == _session.StoreId && registerIds.Contains(r.Id))
            .Select(r => new RegisterSyncDto(r.Id, r.Number, r.Name, r.IsActive, r.SyncVersion, r.CreatedAt, r.UpdatedAt, r.IsDeleted))
            .ToListAsync(cancellationToken);

        var payload = OrderByGuidSequence(changes, registers, item => item.RegisterId);

        if (payload.Count == 0)
            return new RegisterSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new RegisterSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var response = await client.PostAsJsonAsync("api/sync/registers/push", new RegisterSyncBatchDto(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new RegisterSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var result = await response.Content.ReadFromJsonAsync<RegisterSyncPushResultDto>(cancellationToken: cancellationToken)
            ?? new RegisterSyncPushResultDto(Array.Empty<RegisterSyncItemResultDto>());

        var nextCursor = ComputeNextGuidSequenceCursor(
            changes,
            result.Results,
            normalizedSinceVersion,
            row => row.RegisterId,
            row => row.Status);

        if (!string.Equals(nextCursor, normalizedSinceVersion, StringComparison.Ordinal))
        {
            await UpsertCursorAsync(db, RegisterPushCursorSettingKey, nextCursor, EmptySequenceCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var sent = result.Results.Count(x => string.Equals(x.Status, "Applied", StringComparison.OrdinalIgnoreCase));
        var skippedCount = result.Results.Count(x => string.Equals(x.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
        var failedCount = payload.Count - sent - skippedCount;

        return new RegisterSyncPushRunResultDto(payload.Count, sent, skippedCount, failedCount, nextCursor);
    }

    public async Task<RegisterSyncPullRunResultDto> PullRemoteRegistersAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new RegisterSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new RegisterSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new RegisterSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, RegisterPullCursorSettingKey, EmptySequenceCursor, cancellationToken);
        var pullResponse = await client.GetAsync(
            $"api/sync/registers/pull?sinceVersion={Uri.EscapeDataString(sinceVersion)}&batchSize={batchSize}",
            cancellationToken);

        if (!pullResponse.IsSuccessStatusCode)
            return new RegisterSyncPullRunResultDto(0, 0, 0, 0, sinceVersion);

        var payload = await pullResponse.Content.ReadFromJsonAsync<RegisterSyncPullResultDto>(cancellationToken: cancellationToken)
            ?? new RegisterSyncPullResultDto(Array.Empty<RegisterSyncDto>(), sinceVersion);

        if (payload.Registers.Count == 0)
        {
            if (!string.Equals(payload.NextSinceVersion, sinceVersion, StringComparison.Ordinal))
            {
                await UpsertCursorAsync(db, RegisterPullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new RegisterSyncPullRunResultDto(0, 0, 0, 0, payload.NextSinceVersion);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var applied = 0;
            var skipped = 0;
            var tenantId = await GetTenantIdAsync(db, cancellationToken);

            foreach (var incoming in payload.Registers)
            {
                var existing = await db.Registers.FirstOrDefaultAsync(r => r.Id == incoming.RegisterId, cancellationToken);
                if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
                {
                    skipped++;
                    continue;
                }

                if (existing is null)
                {
                    db.Registers.Add(new Register
                    {
                        Id = incoming.RegisterId,
                        TenantId = tenantId,
                        StoreId = _session.StoreId,
                        Number = incoming.Number,
                        Name = incoming.Name,
                        IsActive = incoming.IsActive,
                        SyncVersion = incoming.SyncVersion,
                        CreatedAt = incoming.CreatedAt,
                        UpdatedAt = incoming.UpdatedAt,
                        IsDeleted = incoming.IsDeleted
                    });
                }
                else
                {
                    existing.Number = incoming.Number;
                    existing.Name = incoming.Name;
                    existing.IsActive = incoming.IsActive;
                    existing.SyncVersion = incoming.SyncVersion;
                    existing.UpdatedAt = incoming.UpdatedAt;
                    existing.IsDeleted = incoming.IsDeleted;
                }

                applied++;
            }

            await UpsertCursorAsync(db, RegisterPullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new RegisterSyncPullRunResultDto(payload.Registers.Count, applied, skipped, 0, payload.NextSinceVersion);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            return new RegisterSyncPullRunResultDto(payload.Registers.Count, 0, 0, payload.Registers.Count, sinceVersion);
        }
    }

    public async Task<InvoiceSyncPullRunResultDto> PullRemoteInvoicesAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new InvoiceSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new InvoiceSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new InvoiceSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, InvoicePullCursorSettingKey, EmptySequenceCursor, cancellationToken);
        var pullResponse = await client.GetAsync(
            $"api/sync/invoices/pull?sinceVersion={Uri.EscapeDataString(sinceVersion)}&batchSize={batchSize}",
            cancellationToken);

        if (!pullResponse.IsSuccessStatusCode)
            return new InvoiceSyncPullRunResultDto(0, 0, 0, 0, sinceVersion);

        var payload = await pullResponse.Content.ReadFromJsonAsync<InvoiceSyncPullResultDto>(cancellationToken: cancellationToken)
            ?? new InvoiceSyncPullResultDto(Array.Empty<InvoiceSyncInvoiceDto>(), sinceVersion);

        if (payload.Invoices.Count == 0)
        {
            if (!string.Equals(payload.NextSinceVersion, sinceVersion, StringComparison.Ordinal))
            {
                await UpsertCursorAsync(db, InvoicePullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new InvoiceSyncPullRunResultDto(0, 0, 0, 0, payload.NextSinceVersion);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var applied = 0;
            var skipped = 0;
            var tenantId = await GetTenantIdAsync(db, cancellationToken);

            foreach (var incoming in payload.Invoices)
            {
                if (!await HasAllInvoiceProductsAsync(db, incoming, cancellationToken))
                {
                    skipped++;
                    continue;
                }

                var resolvedUserId = await ResolveInvoiceUserIdAsync(db, incoming.Username, cancellationToken);
                if (resolvedUserId is null)
                {
                    // Username was supplied but doesn't resolve locally yet — skip rather than
                    // misattribute; this batch's cursor still advances regardless (same eventual-
                    // consistency behavior as the "missing product" skip case above), so this invoice
                    // is picked up correctly on a later pass once the user has synced down.
                    skipped++;
                    continue;
                }
                var userId = resolvedUserId.Value;
                var device = await GetOrCreateSyncDeviceAsync(db, _session.StoreId, incoming, cancellationToken);
                var existing = await db.Invoices
                    .Include(i => i.Items)
                    .Include(i => i.Payments)
                    .FirstOrDefaultAsync(i => i.Id == incoming.InvoiceId && i.StoreId == _session.StoreId && !i.IsDeleted, cancellationToken);

                if (existing is null)
                {
                    var created = CreateInvoiceFromSync(incoming, tenantId, _session.StoreId, userId, device.Id);
                    db.Invoices.Add(created);
                    await ReconcileInvoiceInventoryAsync(
                        db,
                        tenantId,
                        _session.StoreId,
                        userId,
                        created.Id,
                        incoming.UpdatedAt,
                        new Dictionary<Guid, decimal>(),
                        BuildInventoryEffect(created.Status, created.Items),
                        cancellationToken);
                    applied++;
                    continue;
                }

                if (existing.SyncVersion > incoming.SyncVersion)
                {
                    skipped++;
                    continue;
                }

                if (existing.SyncVersion == incoming.SyncVersion)
                {
                    existing.IsSynced = true;
                    skipped++;
                    continue;
                }

                var previousInventoryEffect = BuildInventoryEffect(existing.Status, existing.Items);
                ApplyInvoiceSnapshot(existing, incoming, userId, device.Id);
                await ReconcileInvoiceInventoryAsync(
                    db,
                    tenantId,
                    _session.StoreId,
                    userId,
                    existing.Id,
                    incoming.UpdatedAt,
                    previousInventoryEffect,
                    BuildInventoryEffect(existing.Status, existing.Items),
                    cancellationToken);
                applied++;
            }

            await UpsertCursorAsync(db, InvoicePullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new InvoiceSyncPullRunResultDto(payload.Invoices.Count, applied, skipped, 0, payload.NextSinceVersion);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            return new InvoiceSyncPullRunResultDto(payload.Invoices.Count, 0, 0, payload.Invoices.Count, sinceVersion);
        }
    }

    /// <summary>
    /// Pushes CashSession aggregates (header + every movement) that changed locally since the cursor —
    /// opening, manual cash in/out (with approval), sale/refund receipts, and closing counts/discrepancy
    /// all move as one atomic snapshot per session, the same way Invoice pushes its Items/Payments
    /// nested. Must run after invoice push and register push (see the interface doc) so the FKs a
    /// movement/session snapshot references already exist server-side.
    /// </summary>
    public async Task<CashSessionSyncPushRunResultDto> PushUpdatedCashSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new CashSessionSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new CashSessionSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, CashSessionPushCursorSettingKey, EmptySequenceCursor, cancellationToken);
        if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
            return new CashSessionSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var changes = await GetGuidSyncChangesAsync(
            db,
            SyncAggregateTypes.CashSession,
            sinceSequence,
            batchSize,
            _session.StoreId,
            includeGlobalRows: false,
            cancellationToken);

        if (changes.Count == 0)
            return new CashSessionSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var sessionIds = changes.Select(change => change.EntityId).ToList();
        var sessions = await BuildCashSessionSyncDtosAsync(db, sessionIds, cancellationToken);

        var payload = OrderByGuidSequence(changes, sessions, item => item.CashSessionId);

        if (payload.Count == 0)
            return new CashSessionSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new CashSessionSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var response = await client.PostAsJsonAsync("api/sync/cash-sessions/push", new CashSessionSyncBatchDto(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new CashSessionSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var result = await response.Content.ReadFromJsonAsync<CashSessionSyncPushResultDto>(cancellationToken: cancellationToken)
            ?? new CashSessionSyncPushResultDto(Array.Empty<CashSessionSyncItemResultDto>());

        var nextCursor = ComputeNextGuidSequenceCursor(
            changes,
            result.Results,
            normalizedSinceVersion,
            row => row.CashSessionId,
            row => row.Status);

        if (!string.Equals(nextCursor, normalizedSinceVersion, StringComparison.Ordinal))
        {
            await UpsertCursorAsync(db, CashSessionPushCursorSettingKey, nextCursor, EmptySequenceCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var sent = result.Results.Count(x => string.Equals(x.Status, "Applied", StringComparison.OrdinalIgnoreCase));
        var skippedCount = result.Results.Count(x => string.Equals(x.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
        var failedCount = payload.Count - sent - skippedCount;

        return new CashSessionSyncPushRunResultDto(payload.Count, sent, skippedCount, failedCount, nextCursor);
    }

    public async Task<CashSessionSyncPullRunResultDto> PullRemoteCashSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new CashSessionSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new CashSessionSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new CashSessionSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, CashSessionPullCursorSettingKey, EmptySequenceCursor, cancellationToken);
        var pullResponse = await client.GetAsync(
            $"api/sync/cash-sessions/pull?sinceVersion={Uri.EscapeDataString(sinceVersion)}&batchSize={batchSize}",
            cancellationToken);

        if (!pullResponse.IsSuccessStatusCode)
            return new CashSessionSyncPullRunResultDto(0, 0, 0, 0, sinceVersion);

        var payload = await pullResponse.Content.ReadFromJsonAsync<CashSessionSyncPullResultDto>(cancellationToken: cancellationToken)
            ?? new CashSessionSyncPullResultDto(Array.Empty<CashSessionSyncDto>(), sinceVersion);

        if (payload.Sessions.Count == 0)
        {
            if (!string.Equals(payload.NextSinceVersion, sinceVersion, StringComparison.Ordinal))
            {
                await UpsertCursorAsync(db, CashSessionPullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new CashSessionSyncPullRunResultDto(0, 0, 0, 0, payload.NextSinceVersion);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var applied = 0;
            var skipped = 0;
            var tenantId = await GetTenantIdAsync(db, cancellationToken);

            foreach (var incoming in payload.Sessions)
            {
                // The Register FK is required server-side (and locally) — must already exist (register
                // sync runs before this in the background service's ordering).
                if (!await db.Registers.AnyAsync(r => r.Id == incoming.RegisterId, cancellationToken))
                {
                    skipped++;
                    continue;
                }

                var openedByUserId = await ResolveCashActorUserIdAsync(db, incoming.OpenedByUserId, incoming.OpenedByUsername, cancellationToken);
                if (openedByUserId is null)
                {
                    // Same rule as invoice actor resolution: never misattribute — skip and retry once
                    // the user has synced down.
                    skipped++;
                    continue;
                }

                Guid? closedByUserId = null;
                if (!string.IsNullOrWhiteSpace(incoming.ClosedByUsername) || incoming.ClosedByUserId is not null)
                {
                    closedByUserId = await ResolveCashActorUserIdAsync(db, incoming.ClosedByUserId, incoming.ClosedByUsername, cancellationToken);
                    if (closedByUserId is null)
                    {
                        skipped++;
                        continue;
                    }
                }

                var existing = await db.CashSessions.FirstOrDefaultAsync(s => s.Id == incoming.CashSessionId, cancellationToken);

                if (existing is null)
                {
                    var created = new CashSession
                    {
                        Id = incoming.CashSessionId,
                        TenantId = tenantId,
                        StoreId = _session.StoreId,
                        RegisterId = incoming.RegisterId,
                        OpenedByUserId = openedByUserId.Value,
                        OpenedAt = incoming.OpenedAt,
                        OpeningCashAmount = incoming.OpeningCashAmount,
                        CurrencyCode = incoming.CurrencyCode,
                        Status = incoming.Status,
                        IsSharedSession = incoming.IsSharedSession,
                        ClosedByUserId = closedByUserId,
                        ClosedAt = incoming.ClosedAt,
                        ClosingCountedAmount = incoming.ClosingCountedAmount,
                        ExpectedCashAmount = incoming.ExpectedCashAmount,
                        DiscrepancyAmount = incoming.DiscrepancyAmount,
                        Notes = incoming.Notes,
                        IsSynced = true,
                        SyncVersion = incoming.SyncVersion,
                        CreatedAt = incoming.CreatedAt,
                        UpdatedAt = incoming.UpdatedAt,
                        IsDeleted = incoming.IsDeleted
                    };
                    db.CashSessions.Add(created);
                    await ReconcileCashMovementsAsync(db, created.Id, tenantId, incoming.Movements, cancellationToken);
                    applied++;
                    continue;
                }

                // Movements are append-only and idempotent by Id — reconcile them unconditionally, before
                // the header's own version outcome below, so a header Conflict/Skip can never discard a
                // legitimate movement the server already accepted from another device.
                await ReconcileCashMovementsAsync(db, existing.Id, tenantId, incoming.Movements, cancellationToken);

                if (existing.SyncVersion > incoming.SyncVersion)
                {
                    skipped++;
                    continue;
                }

                if (existing.SyncVersion == incoming.SyncVersion)
                {
                    existing.IsSynced = true;
                    skipped++;
                    continue;
                }

                // incoming.SyncVersion is strictly higher than this device's own record — ordinarily
                // "apply it," but a closed session's header is an immutable posted fact: once this device
                // has recorded it as Closed, no incoming snapshot may reopen it or rewrite its closing
                // figures, regardless of SyncVersion (mirrors the server-side rule in Program.cs).
                if (existing.Status == CashSessionStatus.Closed)
                {
                    skipped++;
                    continue;
                }

                existing.RegisterId = incoming.RegisterId;
                existing.OpenedByUserId = openedByUserId.Value;
                existing.OpenedAt = incoming.OpenedAt;
                existing.OpeningCashAmount = incoming.OpeningCashAmount;
                existing.CurrencyCode = incoming.CurrencyCode;
                existing.Status = incoming.Status;
                existing.IsSharedSession = incoming.IsSharedSession;
                existing.ClosedByUserId = closedByUserId;
                existing.ClosedAt = incoming.ClosedAt;
                existing.ClosingCountedAmount = incoming.ClosingCountedAmount;
                existing.ExpectedCashAmount = incoming.ExpectedCashAmount;
                existing.DiscrepancyAmount = incoming.DiscrepancyAmount;
                existing.Notes = incoming.Notes;
                existing.IsSynced = true;
                existing.SyncVersion = incoming.SyncVersion;
                existing.UpdatedAt = incoming.UpdatedAt;
                existing.IsDeleted = incoming.IsDeleted;

                applied++;
            }

            await UpsertCursorAsync(db, CashSessionPullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new CashSessionSyncPullRunResultDto(payload.Sessions.Count, applied, skipped, 0, payload.NextSinceVersion);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            return new CashSessionSyncPullRunResultDto(payload.Sessions.Count, 0, 0, payload.Sessions.Count, sinceVersion);
        }
    }

    /// <summary>
    /// Inserts whichever incoming movements aren't already present locally by Id — movements are
    /// append-only, so there is no update case, and re-applying the same snapshot twice (a retried push,
    /// or the same version pulled again) can never double-record one: it's already there, so it's
    /// skipped. Each movement's actor (and approver, if any) is resolved by username and never
    /// misattributed; a movement whose actor doesn't resolve yet, or whose Invoice/Payment reference
    /// hasn't synced down yet, is skipped individually — the session's own header (including its
    /// authoritative ExpectedCashAmount/DiscrepancyAmount, computed once at close time on the
    /// originating device) still applies, and the movement fills in on a later pass.
    /// </summary>
    private async Task ReconcileCashMovementsAsync(
        PosDbContext db,
        Guid cashSessionId,
        Guid tenantId,
        IReadOnlyList<CashMovementSyncDto> incomingMovements,
        CancellationToken cancellationToken)
    {
        if (incomingMovements.Count == 0)
            return;

        var incomingIds = incomingMovements.Select(m => m.MovementId).ToList();
        var existingIds = (await db.CashMovements
                .AsNoTracking()
                .Where(m => m.CashSessionId == cashSessionId && incomingIds.Contains(m.Id))
                .Select(m => m.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var incoming in incomingMovements)
        {
            if (existingIds.Contains(incoming.MovementId))
                continue;

            if (incoming.InvoiceId is { } invoiceId && !await db.Invoices.AnyAsync(i => i.Id == invoiceId, cancellationToken))
                continue;

            if (incoming.PaymentId is { } paymentId && !await db.Payments.AnyAsync(p => p.Id == paymentId, cancellationToken))
                continue;

            var performedByUserId = await ResolveCashActorUserIdAsync(db, incoming.PerformedByUserId, incoming.PerformedByUsername, cancellationToken);
            if (performedByUserId is null)
                continue;

            Guid? approvedByUserId = null;
            if (!string.IsNullOrWhiteSpace(incoming.ApprovedByUsername) || incoming.ApprovedByUserId is not null)
            {
                approvedByUserId = await ResolveCashActorUserIdAsync(db, incoming.ApprovedByUserId, incoming.ApprovedByUsername, cancellationToken);
                if (approvedByUserId is null)
                    continue; // An approval must never silently become "no approver" — skip and retry.

                // A resolved approver id is not itself proof of authorization — never apply a cash-out
                // "approved" by an unauthorized or self-approving user.
                if (!await IsAuthorizedCashApproverAsync(db, approvedByUserId.Value, performedByUserId.Value, cancellationToken))
                    continue;
            }

            db.CashMovements.Add(new CashMovement
            {
                Id = incoming.MovementId,
                TenantId = tenantId,
                CashSessionId = cashSessionId,
                Type = incoming.Type,
                Amount = incoming.Amount,
                Method = incoming.Method,
                CurrencyCode = incoming.CurrencyCode,
                InvoiceId = incoming.InvoiceId,
                PaymentId = incoming.PaymentId,
                PerformedByUserId = performedByUserId.Value,
                ApprovedByUserId = approvedByUserId,
                Notes = incoming.Notes,
                CreatedAt = incoming.CreatedAt,
                UpdatedAt = incoming.CreatedAt
            });
        }
    }

    /// <summary>Resolves a cash-session actor (opener/closer/performer/approver). Tries the remote
    /// device's claimed <paramref name="userIdHint"/> first — never trusted on its own, only accepted once
    /// verified against a real, non-deleted user of this same local store — so a later username change on
    /// the server can never strand a session this device is still pulling. Falls back to a username match
    /// for degenerate/pre-migration payloads with no id. Returns null (never any other fallback) when
    /// neither resolves, so the caller can skip rather than misattribute.</summary>
    private async Task<Guid?> ResolveCashActorUserIdAsync(PosDbContext db, Guid? userIdHint, string? username, CancellationToken cancellationToken)
    {
        if (userIdHint is { } id && id != Guid.Empty)
        {
            var byId = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == id && u.StoreId == _session.StoreId && !u.IsDeleted)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(username))
            return null;

        return await db.Users
            .AsNoTracking()
            .Where(u => u.StoreId == _session.StoreId && u.Username == username.Trim() && !u.IsDeleted)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>A resolved approver id is not itself proof of authorization — mirrors
    /// <c>CashSessionService.RecordManualMovementAsync</c>'s local rule so a pulled movement can never
    /// apply a cash-out "approved" by an unauthorized or self-approving user.</summary>
    private static async Task<bool> IsAuthorizedCashApproverAsync(PosDbContext db, Guid approverUserId, Guid performerUserId, CancellationToken cancellationToken)
    {
        if (approverUserId == performerUserId)
            return false;

        var permissions = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == approverUserId && u.IsActive && !u.IsDeleted)
            .Select(u => (int?)u.Role!.PermissionsMask)
            .FirstOrDefaultAsync(cancellationToken);

        return permissions is not null && ((Permission)permissions.Value).HasFlag(Permission.ManageSettings);
    }

    private static async Task<List<CashSessionSyncDto>> BuildCashSessionSyncDtosAsync(
        PosDbContext db,
        IReadOnlyCollection<Guid> sessionIds,
        CancellationToken cancellationToken)
    {
        var sessions = await db.CashSessions
            .AsNoTracking()
            .Where(s => sessionIds.Contains(s.Id))
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
            return new List<CashSessionSyncDto>();

        var movements = await db.CashMovements
            .AsNoTracking()
            .Where(m => sessionIds.Contains(m.CashSessionId) && !m.IsDeleted)
            .ToListAsync(cancellationToken);

        var userIds = sessions.Select(s => s.OpenedByUserId)
            .Concat(sessions.Where(s => s.ClosedByUserId.HasValue).Select(s => s.ClosedByUserId!.Value))
            .Concat(movements.Select(m => m.PerformedByUserId))
            .Concat(movements.Where(m => m.ApprovedByUserId.HasValue).Select(m => m.ApprovedByUserId!.Value))
            .Distinct()
            .ToList();
        var usernames = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, cancellationToken);

        var movementsBySession = movements.GroupBy(m => m.CashSessionId).ToDictionary(g => g.Key, g => g.ToList());

        return sessions.Select(s => new CashSessionSyncDto(
            s.Id,
            s.RegisterId,
            s.SyncVersion,
            s.OpenedByUserId,
            usernames.GetValueOrDefault(s.OpenedByUserId, string.Empty),
            s.OpenedAt,
            s.OpeningCashAmount,
            s.CurrencyCode,
            s.Status,
            s.IsSharedSession,
            s.ClosedByUserId,
            s.ClosedByUserId.HasValue ? usernames.GetValueOrDefault(s.ClosedByUserId.Value) : null,
            s.ClosedAt,
            s.ClosingCountedAmount,
            s.ExpectedCashAmount,
            s.DiscrepancyAmount,
            s.Notes,
            s.CreatedAt,
            s.UpdatedAt,
            s.IsDeleted,
            (movementsBySession.TryGetValue(s.Id, out var sessionMovements) ? sessionMovements : new List<CashMovement>())
                .OrderBy(m => m.CreatedAt)
                .Select(m => new CashMovementSyncDto(
                    m.Id,
                    m.Type,
                    m.Amount,
                    m.Method,
                    m.CurrencyCode,
                    m.InvoiceId,
                    m.PaymentId,
                    m.PerformedByUserId,
                    usernames.GetValueOrDefault(m.PerformedByUserId, string.Empty),
                    m.ApprovedByUserId,
                    m.ApprovedByUserId.HasValue ? usernames.GetValueOrDefault(m.ApprovedByUserId.Value) : null,
                    m.Notes,
                    m.CreatedAt))
                .ToList()))
            .ToList();
    }

    public async Task<ProductSyncPushRunResultDto> PushUpdatedProductsAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new ProductSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new ProductSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, ProductPushCursorSettingKey, EmptySequenceCursor, cancellationToken);
        if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
            return new ProductSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var changes = await GetGuidSyncChangesAsync(
            db,
            SyncAggregateTypes.Product,
            sinceSequence,
            batchSize,
            storeId: null,
            includeGlobalRows: false,
            cancellationToken);

        if (changes.Count == 0)
            return new ProductSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var productIds = changes.Select(change => change.EntityId).ToArray();
        var products = await db.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new ProductSyncDto(
                p.Id,
                p.Name,
                p.Barcode,
                p.Price,
                p.Cost,
                p.CategoryId,
                p.Category != null ? p.Category.Name : string.Empty,
                db.Inventories
                    .Where(i => i.StoreId == _session.StoreId && i.ProductId == p.Id && !i.IsDeleted)
                    .Select(i => (decimal?)i.Quantity)
                    .FirstOrDefault() ?? 0m,
                p.IsActive,
                p.ImagePath,
                p.CreatedAt,
                p.UpdatedAt,
                p.IsDeleted))
            .ToListAsync(cancellationToken);

        var payload = OrderByGuidSequence(changes, products, item => item.ProductId);

        if (payload.Count == 0)
            return new ProductSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new ProductSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var response = await client.PostAsJsonAsync("api/sync/products/push", new ProductSyncBatchDto(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new ProductSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var result = await response.Content.ReadFromJsonAsync<ProductSyncPushResultDto>(cancellationToken: cancellationToken)
            ?? new ProductSyncPushResultDto(Array.Empty<ProductSyncItemResultDto>());

        var nextCursor = ComputeNextGuidSequenceCursor(
            changes,
            result.Results,
            normalizedSinceVersion,
            row => row.ProductId,
            row => row.Status);

        if (!string.Equals(nextCursor, normalizedSinceVersion, StringComparison.Ordinal))
        {
            await UpsertCursorAsync(db, ProductPushCursorSettingKey, nextCursor, EmptySequenceCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var sent = result.Results.Count(x => string.Equals(x.Status, "Applied", StringComparison.OrdinalIgnoreCase));
        var skipped = result.Results.Count(x => string.Equals(x.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
        var failed = payload.Count - sent - skipped;

        return new ProductSyncPushRunResultDto(payload.Count, sent, skipped, failed, nextCursor);
    }

    public async Task<SettingsSyncPushRunResultDto> PushUpdatedSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new SettingsSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new SettingsSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, SettingsPushCursorSettingKey, EmptySequenceCursor, cancellationToken);
        if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
            return new SettingsSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var changes = await GetKeySyncChangesAsync(
            db,
            SyncAggregateTypes.Setting,
            sinceSequence,
            batchSize,
            _session.StoreId,
            includeGlobalRows: false,
            cancellationToken);

        if (changes.Count == 0)
            return new SettingsSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var settingKeys = changes.Select(change => change.EntityKey).ToArray();
        var settings = await db.Settings
            .AsNoTracking()
            .Where(s => s.StoreId == _session.StoreId && !s.Key.StartsWith("Sync.") && settingKeys.Contains(s.Key))
            .Select(s => new SettingSyncDto(
                s.Key,
                s.Value,
                s.CreatedAt,
                s.UpdatedAt,
                s.IsDeleted))
            .ToListAsync(cancellationToken);

        var payload = OrderByKeySequence(changes, settings, item => item.Key);

        if (payload.Count == 0)
            return new SettingsSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new SettingsSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var response = await client.PostAsJsonAsync("api/sync/settings/push", new SettingsSyncBatchDto(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new SettingsSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var result = await response.Content.ReadFromJsonAsync<SettingsSyncPushResultDto>(cancellationToken: cancellationToken)
            ?? new SettingsSyncPushResultDto(Array.Empty<SettingSyncItemResultDto>());

        var nextCursor = ComputeNextKeySequenceCursor(
            changes,
            result.Results,
            normalizedSinceVersion,
            row => row.Key,
            row => row.Status);

        if (!string.Equals(nextCursor, normalizedSinceVersion, StringComparison.Ordinal))
        {
            await UpsertCursorAsync(db, SettingsPushCursorSettingKey, nextCursor, EmptySequenceCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var sent = result.Results.Count(x => string.Equals(x.Status, "Applied", StringComparison.OrdinalIgnoreCase));
        var skipped = result.Results.Count(x => string.Equals(x.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
        var failed = payload.Count - sent - skipped;

        return new SettingsSyncPushRunResultDto(payload.Count, sent, skipped, failed, nextCursor);
    }

    public async Task<UserSyncPushRunResultDto> PushUpdatedUsersAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new UserSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new UserSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, UserPushCursorSettingKey, EmptySequenceCursor, cancellationToken);
        if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
            return new UserSyncPushRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var changes = await GetGuidSyncChangesAsync(
            db,
            SyncAggregateTypes.User,
            sinceSequence,
            batchSize,
            _session.StoreId,
            includeGlobalRows: false,
            cancellationToken);

        if (changes.Count == 0)
            return new UserSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var userIds = changes.Select(change => change.EntityId).ToArray();
        var users = await db.Users
            .AsNoTracking()
            .Where(u => u.StoreId == _session.StoreId && userIds.Contains(u.Id))
            .Select(u => new UserSyncDto(
                u.Id,
                u.Username,
                u.PasswordHash,
                u.Role != null ? u.Role.Name : string.Empty,
                u.IsActive,
                u.CreatedAt,
                u.UpdatedAt,
                u.IsDeleted,
                u.Role != null ? u.Role.PermissionsMask : 0,
                u.Role != null ? u.Role.UpdatedAt : default))
            .ToListAsync(cancellationToken);

        var payload = OrderByGuidSequence(changes, users, item => item.UserId);

        if (payload.Count == 0)
            return new UserSyncPushRunResultDto(0, 0, 0, 0, normalizedSinceVersion);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new UserSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var response = await client.PostAsJsonAsync("api/sync/users/push", new UserSyncBatchDto(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new UserSyncPushRunResultDto(payload.Count, 0, 0, payload.Count, normalizedSinceVersion);

        var result = await response.Content.ReadFromJsonAsync<UserSyncPushResultDto>(cancellationToken: cancellationToken)
            ?? new UserSyncPushResultDto(Array.Empty<UserSyncItemResultDto>());

        var nextCursor = ComputeNextGuidSequenceCursor(
            changes,
            result.Results,
            normalizedSinceVersion,
            row => row.UserId,
            row => row.Status);

        if (!string.Equals(nextCursor, normalizedSinceVersion, StringComparison.Ordinal))
        {
            await UpsertCursorAsync(db, UserPushCursorSettingKey, nextCursor, EmptySequenceCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var sent = result.Results.Count(x => string.Equals(x.Status, "Applied", StringComparison.OrdinalIgnoreCase));
        var skipped = result.Results.Count(x => string.Equals(x.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
        var failed = payload.Count - sent - skipped;

        return new UserSyncPushRunResultDto(payload.Count, sent, skipped, failed, nextCursor);
    }

    public async Task<ProductSyncPullRunResultDto> PullRemoteProductsAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new ProductSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new ProductSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new ProductSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, ProductPullCursorSettingKey, EmptySequenceCursor, cancellationToken);
        var pullResponse = await client.GetAsync(
            $"api/sync/products/pull?sinceVersion={Uri.EscapeDataString(sinceVersion)}&batchSize={batchSize}",
            cancellationToken);

        if (!pullResponse.IsSuccessStatusCode)
            return new ProductSyncPullRunResultDto(0, 0, 0, 0, sinceVersion);

        var payload = await pullResponse.Content.ReadFromJsonAsync<ProductSyncPullResultDto>(cancellationToken: cancellationToken)
            ?? new ProductSyncPullResultDto(Array.Empty<ProductSyncDto>(), sinceVersion);

        if (payload.Products.Count == 0)
        {
            if (!string.Equals(payload.NextSinceVersion, sinceVersion, StringComparison.Ordinal))
            {
                await UpsertCursorAsync(db, ProductPullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new ProductSyncPullRunResultDto(0, 0, 0, 0, payload.NextSinceVersion);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var applied = 0;
            var skipped = 0;

            foreach (var incoming in payload.Products)
            {
                var existing = await db.Products.FirstOrDefaultAsync(p => p.Id == incoming.ProductId, cancellationToken);
                if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
                {
                    skipped++;
                    continue;
                }

                await EnsureCategoryAsync(db, incoming.CategoryId, incoming.CategoryName, incoming.CreatedAt, incoming.UpdatedAt, cancellationToken);

                if (existing is null)
                {
                    existing = new Product
                    {
                        Id = incoming.ProductId,
                        TenantId = await GetTenantIdAsync(db, cancellationToken),
                        CreatedAt = incoming.CreatedAt
                    };
                    db.Products.Add(existing);
                }

                ApplyProductSnapshot(existing, incoming);
                await UpsertProductInventoryAsync(db, incoming.ProductId, incoming.QuantityOnHand, incoming.UpdatedAt, cancellationToken);
                applied++;
            }

            await UpsertCursorAsync(db, ProductPullCursorSettingKey, payload.NextSinceVersion, EmptyGuidCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new ProductSyncPullRunResultDto(payload.Products.Count, applied, skipped, 0, payload.NextSinceVersion);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            return new ProductSyncPullRunResultDto(payload.Products.Count, 0, 0, payload.Products.Count, sinceVersion);
        }
    }

    public async Task<SettingsSyncPullRunResultDto> PullRemoteSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new SettingsSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new SettingsSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new SettingsSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, SettingsPullCursorSettingKey, EmptySequenceCursor, cancellationToken);
        var pullResponse = await client.GetAsync(
            $"api/sync/settings/pull?sinceVersion={Uri.EscapeDataString(sinceVersion)}&batchSize={batchSize}",
            cancellationToken);

        if (!pullResponse.IsSuccessStatusCode)
            return new SettingsSyncPullRunResultDto(0, 0, 0, 0, sinceVersion);

        var payload = await pullResponse.Content.ReadFromJsonAsync<SettingsSyncPullResultDto>(cancellationToken: cancellationToken)
            ?? new SettingsSyncPullResultDto(Array.Empty<SettingSyncDto>(), sinceVersion);

        if (payload.Settings.Count == 0)
        {
            if (!string.Equals(payload.NextSinceVersion, sinceVersion, StringComparison.Ordinal))
            {
                await UpsertCursorAsync(db, SettingsPullCursorSettingKey, payload.NextSinceVersion, EmptyKeyCursor, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new SettingsSyncPullRunResultDto(0, 0, 0, 0, payload.NextSinceVersion);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var applied = 0;
            var skipped = 0;

            foreach (var incoming in payload.Settings)
            {
                if (incoming.Key.StartsWith("Sync.", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                var existing = await db.Settings
                    .FirstOrDefaultAsync(s => s.StoreId == _session.StoreId && s.Key == incoming.Key, cancellationToken);

                if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
                {
                    skipped++;
                    continue;
                }

                if (existing is null)
                {
                    db.Settings.Add(new Setting
                    {
                        Id = Guid.NewGuid(),
                        TenantId = await GetTenantIdAsync(db, cancellationToken),
                        StoreId = _session.StoreId,
                        Key = incoming.Key,
                        Value = incoming.Value,
                        CreatedAt = incoming.CreatedAt,
                        UpdatedAt = incoming.UpdatedAt,
                        IsDeleted = incoming.IsDeleted
                    });
                }
                else
                {
                    existing.Value = incoming.Value;
                    existing.CreatedAt = incoming.CreatedAt;
                    existing.UpdatedAt = incoming.UpdatedAt;
                    existing.IsDeleted = incoming.IsDeleted;
                }

                applied++;
            }

            await UpsertCursorAsync(db, SettingsPullCursorSettingKey, payload.NextSinceVersion, EmptyKeyCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new SettingsSyncPullRunResultDto(payload.Settings.Count, applied, skipped, 0, payload.NextSinceVersion);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            return new SettingsSyncPullRunResultDto(payload.Settings.Count, 0, 0, payload.Settings.Count, sinceVersion);
        }
    }

    public async Task<UserSyncPullRunResultDto> PullRemoteUsersAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.HasSyncScope)
            return new UserSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var enabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        var apiBaseUrl = _configuration["Sync:ApiBaseUrl"]?.Trim();
        var batchSize = Math.Max(1, _configuration.GetValue<int?>("Sync:BatchSize") ?? DefaultBatchSize);

        if (!enabled || string.IsNullOrWhiteSpace(apiBaseUrl))
            return new UserSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        var client = await TryCreateAuthorizedClientAsync(apiBaseUrl, cancellationToken);
        if (client is null)
            return new UserSyncPullRunResultDto(0, 0, 0, 0, EmptySequenceCursor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sinceVersion = await GetCursorAsync(db, UserPullCursorSettingKey, EmptySequenceCursor, cancellationToken);
        var pullResponse = await client.GetAsync(
            $"api/sync/users/pull?sinceVersion={Uri.EscapeDataString(sinceVersion)}&batchSize={batchSize}",
            cancellationToken);

        if (!pullResponse.IsSuccessStatusCode)
            return new UserSyncPullRunResultDto(0, 0, 0, 0, sinceVersion);

        var payload = await pullResponse.Content.ReadFromJsonAsync<UserSyncPullResultDto>(cancellationToken: cancellationToken)
            ?? new UserSyncPullResultDto(Array.Empty<UserSyncDto>(), sinceVersion);

        if (payload.Users.Count == 0)
        {
            if (!string.Equals(payload.NextSinceVersion, sinceVersion, StringComparison.Ordinal))
            {
                await UpsertCursorAsync(db, UserPullCursorSettingKey, payload.NextSinceVersion, EmptySequenceCursor, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new UserSyncPullRunResultDto(0, 0, 0, 0, payload.NextSinceVersion);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var applied = 0;
            var skipped = 0;

            foreach (var incoming in payload.Users)
            {
                if (string.IsNullOrWhiteSpace(incoming.Username) || string.IsNullOrWhiteSpace(incoming.RoleName))
                {
                    skipped++;
                    continue;
                }

                var existing = await FindLocalUserBySyncIdentityAsync(db, incoming.UserId, incoming.Username, cancellationToken);
                if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
                {
                    skipped++;
                    continue;
                }

                var roleId = await EnsureLocalRoleAsync(db, incoming.RoleName, incoming.RolePermissionsMask, incoming.RoleUpdatedAt, cancellationToken);
                var tenantId = await GetTenantIdAsync(db, cancellationToken);

                if (existing is null)
                {
                    existing = new User
                    {
                        Id = incoming.UserId,
                        TenantId = tenantId,
                        StoreId = _session.StoreId
                    };
                    db.Users.Add(existing);
                }

                ApplyUserSnapshot(existing, incoming, tenantId, roleId, _session.StoreId);
                applied++;
            }

            await UpsertCursorAsync(db, UserPullCursorSettingKey, payload.NextSinceVersion, EmptySequenceCursor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new UserSyncPullRunResultDto(payload.Users.Count, applied, skipped, 0, payload.NextSinceVersion);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            return new UserSyncPullRunResultDto(payload.Users.Count, 0, 0, payload.Users.Count, sinceVersion);
        }
    }

    private async Task<int> MarkLocallySyncedAsync(
        IReadOnlyList<InvoiceSyncInvoiceResultDto> results,
        CancellationToken cancellationToken)
    {
        var accepted = results
            .Where(x => string.Equals(x.Status, "Applied", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(x.Status, "Skipped", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (accepted.Count == 0)
            return 0;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var byId = accepted.ToDictionary(x => x.InvoiceId);
        var invoices = await db.Invoices
            .Where(i => byId.Keys.Contains(i.Id) && !i.IsDeleted)
            .ToListAsync(cancellationToken);

        var marked = 0;
        foreach (var invoice in invoices)
        {
            var result = byId[invoice.Id];
            if (invoice.SyncVersion != result.ServerSyncVersion)
                continue;

            invoice.IsSynced = true;
            invoice.UpdatedAt = DateTime.UtcNow;
            marked++;
        }

        if (marked > 0)
            await db.SaveChangesAsync(cancellationToken);

        return marked;
    }

    private async Task<HttpClient?> TryCreateAuthorizedClientAsync(string apiBaseUrl, CancellationToken cancellationToken)
    {
        if (_cachedAuthorizedClient is not null)
            return _cachedAuthorizedClient;

        // The API now verifies a real password on every login (Stage 4T/T2). The sync worker re-authenticates
        // as the currently signed-in local user, so it needs that same verified password — captured in memory
        // only, at login time, via ICurrentSession.SetPassword — to get its own HTTP session for these calls.
        // No password means no interactive user is signed in right now (e.g. after logout) — fall back to
        // this terminal's own persistent device credential instead of giving up on sync entirely.
        if (string.IsNullOrEmpty(_session.Password))
            return await TryCreateDeviceAuthorizedClientAsync(apiBaseUrl, cancellationToken);

        // The API can only auto-resolve an omitted tenant slug when exactly one active tenant exists
        // system-wide (AuthService.ResolveTenantIdAsync) — true for a lone bootstrap tenant, false the
        // moment a real second tenant is ever provisioned. This re-login previously omitted the slug
        // entirely, so it silently failed closed (401) on any server with more than one tenant — found
        // live during the staging WPF rehearsal, where sync ran every interval without error but never
        // actually pushed anything. The slug is captured once at "join an existing business" time
        // (BusinessJoinService) and persisted as a store-scoped setting for exactly this re-use.
        await using var slugDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantSlug = await slugDb.Settings
            .AsNoTracking()
            .Where(s => s.StoreId == _session.StoreId && s.Key == TenantSlugSettingKey && !s.IsDeleted)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(EnsureTrailingSlash(apiBaseUrl), UriKind.Absolute);

        var loginResponse = await client.PostAsJsonAsync(
            "api/auth/login",
            new { Username = _session.Username, Password = _session.Password, TenantSlug = string.IsNullOrWhiteSpace(tenantSlug) ? null : tenantSlug },
            cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
            return null;

        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken);
        if (login is null || string.IsNullOrWhiteSpace(login.AccessToken))
            return null;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        _cachedAuthorizedClient = client;
        await RecordSuccessfulOnlineContactAsync(cancellationToken);
        return client;
    }

    /// <summary>
    /// Authenticates as this terminal itself via <c>/api/devices/token</c> (tenant.md §5a) instead of an
    /// employee login — used when no interactive user is signed in. Requires this machine to have
    /// completed device enrollment and to still hold its persisted secret locally (written by
    /// <c>DeviceManagementViewModel.PersistDeviceSecretAsync</c> at enrollment time). A device token only
    /// authorizes sync endpoints (<c>SyncPolicy</c> on the API side) — administrative pushes and
    /// currency-policy pull stay user-only by design and will simply fail softly with this client, the
    /// same as any other non-success response each push/pull method already tolerates.
    /// </summary>
    private async Task<HttpClient?> TryCreateDeviceAuthorizedClientAsync(string apiBaseUrl, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;

        var device = await db.Devices
            .AsNoTracking()
            .Where(d => d.StoreId == storeId && d.Name == _currentDevice.Name && !d.IsDeleted && !d.IsRevoked && d.EnrolledAt != null)
            .Select(d => new { d.Id })
            .FirstOrDefaultAsync(cancellationToken);
        if (device is null)
            return null;

        var secret = await db.Settings
            .AsNoTracking()
            .Where(s => s.StoreId == storeId && s.Key == DeviceSecretSettingKey && !s.IsDeleted)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(secret))
            return null;

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(EnsureTrailingSlash(apiBaseUrl), UriKind.Absolute);

        var tokenResponse = await client.PostAsJsonAsync("api/devices/token", new DeviceTokenRequest(device.Id, secret), cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
            return null;

        var token = await tokenResponse.Content.ReadFromJsonAsync<DeviceTokenResponse>(cancellationToken: cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            return null;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        _cachedAuthorizedClient = client;
        await RecordSuccessfulOnlineContactAsync(cancellationToken);
        return client;
    }

    /// <summary>
    /// A successful online login is this device's proof that its user is still active and its
    /// credentials still valid — the exact signal the offline-authorization expiry window (tenant.md
    /// §5) needs. Persisted locally so it survives app restarts, and pushed into the in-memory
    /// session immediately so <see cref="POS.Application.Abstractions.IOfflineAuthorizationPolicy"/>
    /// sees it without a DB round-trip on every permission check.
    /// </summary>
    private async Task RecordSuccessfulOnlineContactAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        _session.SetLastOnlineContactUtc(now);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await UpsertCursorAsync(db, OfflineAuthorizationSettingKeys.LastOnlineContactUtc, now.Ticks.ToString(CultureInfo.InvariantCulture), EmptyTicksCursor, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> GetCursorAsync(
        PosDbContext db,
        string key,
        string emptyValue,
        CancellationToken cancellationToken)
    {
        var value = await db.Settings
            .AsNoTracking()
            .Where(s => s.StoreId == _session.StoreId && s.Key == key && !s.IsDeleted)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(value) ? emptyValue : value.Trim();
    }

    private async Task UpsertCursorAsync(
        PosDbContext db,
        string key,
        string nextSinceVersion,
        string emptyValue,
        CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(nextSinceVersion) ? emptyValue : nextSinceVersion.Trim();
        var setting = await db.Settings
            .FirstOrDefaultAsync(s => s.StoreId == _session.StoreId && s.Key == key && !s.IsDeleted, cancellationToken);

        var now = DateTime.UtcNow;
        if (setting is null)
        {
            db.Settings.Add(new Setting
            {
                Id = Guid.NewGuid(),
                TenantId = await GetTenantIdAsync(db, cancellationToken),
                StoreId = _session.StoreId,
                Key = key,
                Value = normalized,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });
            return;
        }

        setting.Value = normalized;
        setting.UpdatedAt = now;
    }

    private static bool TryParseGuidCursor(string raw, out DateTime? updatedAt, out Guid? entityId)
    {
        updatedAt = null;
        entityId = null;

        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), EmptyGuidCursor, StringComparison.Ordinal))
            return true;

        var parts = raw.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || ticks <= 0 || !Guid.TryParseExact(parts[1], "N", out var parsedId))
            return false;

        updatedAt = new DateTime(ticks, DateTimeKind.Utc);
        entityId = parsedId;
        return true;
    }

    private static bool TryParseStringCursor(string raw, out DateTime? updatedAt, out string? key)
    {
        updatedAt = null;
        key = null;

        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), EmptyKeyCursor, StringComparison.Ordinal))
            return true;

        var parts = raw.Trim().Split(':', 2, StringSplitOptions.None);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || ticks <= 0)
            return false;

        updatedAt = new DateTime(ticks, DateTimeKind.Utc);
        key = parts[1];
        return true;
    }

    private static bool TryParseTicksCursor(string raw, out DateTime? updatedAt)
    {
        updatedAt = null;

        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), EmptyTicksCursor, StringComparison.Ordinal))
            return true;

        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0)
            return false;

        updatedAt = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static bool TryParseSequenceSinceVersion(string? raw, out long sinceSequence, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw.Trim(), EmptySequenceCursor, StringComparison.Ordinal)
            || string.Equals(raw.Trim(), EmptyKeyCursor, StringComparison.Ordinal)
            || string.Equals(raw.Trim(), EmptyGuidCursor, StringComparison.Ordinal)
            || raw.Trim().Contains(':', StringComparison.Ordinal))
        {
            sinceSequence = 0;
            normalized = EmptySequenceCursor;
            return true;
        }

        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSequence) || parsedSequence < 0)
        {
            sinceSequence = 0;
            normalized = EmptySequenceCursor;
            return false;
        }

        sinceSequence = parsedSequence;
        normalized = FormatSequenceSinceVersion(parsedSequence);
        return true;
    }

    private static string FormatSequenceSinceVersion(long sequence) =>
        sequence <= 0
            ? EmptySequenceCursor
            : sequence.ToString(CultureInfo.InvariantCulture);

    private async Task<DateTime> GetCurrencyPolicyUpdatedAtAsync(PosDbContext db, CancellationToken cancellationToken)
    {
        var store = await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == _session.StoreId && !s.IsDeleted)
            .Select(s => new { s.TenantId, s.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        var storeUpdatedAt = store?.UpdatedAt ?? DateTime.MinValue;

        var rateUpdatedAt = store is null
            ? DateTime.MinValue
            : await db.TenantCurrencyRates
                .AsNoTracking()
                .Where(r => r.TenantId == store.TenantId && !r.IsDeleted)
                .Select(r => (DateTime?)r.UpdatedAt)
                .MaxAsync(cancellationToken)
                ?? DateTime.MinValue;

        return storeUpdatedAt >= rateUpdatedAt ? storeUpdatedAt : rateUpdatedAt;
    }

    private async Task<long> GetLatestSyncSequenceAsync(
        PosDbContext db,
        string aggregateType,
        Guid? storeId,
        bool includeGlobalRows,
        CancellationToken cancellationToken)
    {
        return await BuildSyncChangeQuery(db.SyncChanges.AsNoTracking(), aggregateType, 0, storeId, includeGlobalRows)
            .Select(change => (long?)change.Id)
            .MaxAsync(cancellationToken)
            ?? 0;
    }

    private static async Task<List<SequenceGuidChange>> GetGuidSyncChangesAsync(
        PosDbContext db,
        string aggregateType,
        long sinceSequence,
        int take,
        Guid? storeId,
        bool includeGlobalRows,
        CancellationToken cancellationToken)
    {
        var changes = await BuildSyncChangeQuery(db.SyncChanges.AsNoTracking(), aggregateType, sinceSequence, storeId, includeGlobalRows)
            .Where(change => change.EntityId != null)
            .OrderBy(change => change.Id)
            .Take(take)
            .Select(change => new SequenceGuidChange(change.Id, change.EntityId!.Value))
            .ToListAsync(cancellationToken);

        return changes
            .GroupBy(change => change.EntityId)
            .Select(group => group.Last())
            .OrderBy(change => change.Sequence)
            .ToList();
    }

    private static async Task<List<SequenceKeyChange>> GetKeySyncChangesAsync(
        PosDbContext db,
        string aggregateType,
        long sinceSequence,
        int take,
        Guid? storeId,
        bool includeGlobalRows,
        CancellationToken cancellationToken)
    {
        var changes = await BuildSyncChangeQuery(db.SyncChanges.AsNoTracking(), aggregateType, sinceSequence, storeId, includeGlobalRows)
            .Where(change => change.EntityKey != null)
            .OrderBy(change => change.Id)
            .Take(take)
            .Select(change => new SequenceKeyChange(change.Id, change.EntityKey!))
            .ToListAsync(cancellationToken);

        return changes
            .GroupBy(change => change.EntityKey, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(change => change.Sequence)
            .ToList();
    }

    private static IQueryable<SyncChange> BuildSyncChangeQuery(
        IQueryable<SyncChange> query,
        string aggregateType,
        long sinceSequence,
        Guid? storeId,
        bool includeGlobalRows)
    {
        query = query.Where(change => change.AggregateType == aggregateType && change.Id > sinceSequence);

        if (storeId is null)
            return query.Where(change => change.StoreId == null);

        return includeGlobalRows
            ? query.Where(change => change.StoreId == storeId.Value || change.StoreId == null)
            : query.Where(change => change.StoreId == storeId.Value);
    }

    private static List<TItem> OrderByGuidSequence<TItem>(
        IReadOnlyList<SequenceGuidChange> changes,
        IReadOnlyCollection<TItem> items,
        Func<TItem, Guid> itemId)
    {
        var byId = items.ToDictionary(itemId);
        return changes
            .Where(change => byId.ContainsKey(change.EntityId))
            .Select(change => byId[change.EntityId])
            .ToList();
    }

    private static List<TItem> OrderByKeySequence<TItem>(
        IReadOnlyList<SequenceKeyChange> changes,
        IReadOnlyCollection<TItem> items,
        Func<TItem, string> itemKey)
    {
        var byKey = items.ToDictionary(itemKey, StringComparer.Ordinal);
        return changes
            .Where(change => byKey.ContainsKey(change.EntityKey))
            .Select(change => byKey[change.EntityKey])
            .ToList();
    }

    private static string ComputeNextGuidSequenceCursor<TResult>(
        IReadOnlyList<SequenceGuidChange> attempted,
        IReadOnlyList<TResult> results,
        string fallback,
        Func<TResult, Guid> resultId,
        Func<TResult, string> resultStatus)
    {
        var byId = results.ToDictionary(resultId);
        SequenceGuidChange? lastAccepted = null;
        foreach (var change in attempted)
        {
            if (!byId.TryGetValue(change.EntityId, out var result))
                break;

            var status = resultStatus(result);
            if (!string.Equals(status, "Applied", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "Skipped", StringComparison.OrdinalIgnoreCase))
                break;

            lastAccepted = change;
        }

        return lastAccepted is null
            ? fallback
            : FormatSequenceSinceVersion(lastAccepted.Sequence);
    }

    private static string ComputeNextKeySequenceCursor<TResult>(
        IReadOnlyList<SequenceKeyChange> attempted,
        IReadOnlyList<TResult> results,
        string fallback,
        Func<TResult, string> resultKey,
        Func<TResult, string> resultStatus)
    {
        var byKey = results.ToDictionary(resultKey, StringComparer.Ordinal);
        SequenceKeyChange? lastAccepted = null;
        foreach (var change in attempted)
        {
            if (!byKey.TryGetValue(change.EntityKey, out var result))
                break;

            var status = resultStatus(result);
            if (!string.Equals(status, "Applied", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "Skipped", StringComparison.OrdinalIgnoreCase))
                break;

            lastAccepted = change;
        }

        return lastAccepted is null
            ? fallback
            : FormatSequenceSinceVersion(lastAccepted.Sequence);
    }

    private async Task<bool> HasAllInvoiceProductsAsync(
        PosDbContext db,
        InvoiceSyncInvoiceDto incoming,
        CancellationToken cancellationToken)
    {
        var productIds = incoming.Lines
            .Where(line => !line.IsDeleted)
            .Select(line => line.ProductId)
            .Distinct()
            .ToArray();

        if (productIds.Length == 0)
            return true;

        var existingCount = await db.Products
            .AsNoTracking()
            .CountAsync(p => productIds.Contains(p.Id) && !p.IsDeleted, cancellationToken);

        return existingCount == productIds.Length;
    }

    private async Task<Guid> GetTenantIdAsync(PosDbContext db, CancellationToken cancellationToken) =>
        await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == _session.StoreId)
            .Select(s => (Guid?)s.TenantId)
            .FirstOrDefaultAsync(cancellationToken)
        ?? Guid.Empty;

    private async Task EnsureCategoryAsync(
        PosDbContext db,
        Guid categoryId,
        string categoryName,
        DateTime createdAt,
        DateTime updatedAt,
        CancellationToken cancellationToken)
    {
        var existing = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        if (existing is null)
        {
            existing = new Category
            {
                Id = categoryId,
                TenantId = await GetTenantIdAsync(db, cancellationToken)
            };
            db.Categories.Add(existing);
        }

        if (existing.UpdatedAt > updatedAt)
            return;

        ApplyCategorySnapshot(existing, new CategorySyncDto(categoryId, categoryName, createdAt, updatedAt, false));
    }

    private static void ApplyCategorySnapshot(Category existing, CategorySyncDto incoming)
    {
        existing.Name = string.IsNullOrWhiteSpace(incoming.Name)
            ? existing.Name
            : incoming.Name.Trim();
        existing.CreatedAt = incoming.CreatedAt;
        existing.UpdatedAt = incoming.UpdatedAt;
        existing.IsDeleted = incoming.IsDeleted;
    }

    private static void ApplyProductSnapshot(Product existing, ProductSyncDto incoming)
    {
        existing.Name = incoming.Name;
        existing.Barcode = incoming.Barcode;
        existing.Price = incoming.Price;
        existing.Cost = incoming.Cost;
        existing.CategoryId = incoming.CategoryId;
        existing.IsWeighted = false;
        existing.IsActive = incoming.IsActive;
        existing.ImagePath = incoming.ImagePath;
        existing.CreatedAt = incoming.CreatedAt;
        existing.UpdatedAt = incoming.UpdatedAt;
        existing.IsDeleted = incoming.IsDeleted;
    }

    private async Task UpsertProductInventoryAsync(
        PosDbContext db,
        Guid productId,
        decimal quantityOnHand,
        DateTime changedAt,
        CancellationToken cancellationToken)
    {
        var inventory = await db.Inventories
            .FirstOrDefaultAsync(i => i.StoreId == _session.StoreId && i.ProductId == productId, cancellationToken);

        var previousQuantity = inventory?.Quantity ?? 0m;
        var tenantId = await GetTenantIdAsync(db, cancellationToken);
        if (inventory is null)
        {
            inventory = new Inventory
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = productId,
                StoreId = _session.StoreId,
                Quantity = quantityOnHand,
                CreatedAt = changedAt,
                UpdatedAt = changedAt,
                IsDeleted = false
            };
            db.Inventories.Add(inventory);
        }
        else
        {
            inventory.Quantity = quantityOnHand;
            inventory.UpdatedAt = changedAt;
            inventory.IsDeleted = false;
        }

        var delta = quantityOnHand - previousQuantity;
        if (delta == 0)
            return;

        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProductId = productId,
            StoreId = _session.StoreId,
            InventoryId = inventory.Id,
            UserId = _session.UserId == Guid.Empty ? null : _session.UserId,
            Type = StockMovementType.ManualSetAdjustment,
            QuantityDelta = delta,
            QuantityAfter = quantityOnHand,
            Reference = "PRODUCT_SYNC_PULL",
            Notes = "Inventory aligned from pulled product snapshot.",
            CreatedAt = changedAt,
            UpdatedAt = changedAt
        });
    }

    private async Task<Device> UpsertDeviceSnapshotAsync(
        PosDbContext db,
        Guid storeId,
        DeviceSyncDto incoming,
        CancellationToken cancellationToken)
    {
        Device? device = await db.Devices
            .FirstOrDefaultAsync(d => d.Id == incoming.DeviceId && d.StoreId == storeId, cancellationToken);

        if (device is null)
        {
            // No row under this exact Id — a device with the same display Name may already exist (the
            // unique (StoreId, Name) index allows only one row per name), so find it to update in place
            // rather than violating that constraint with a second row of the same name.
            device = await db.Devices
                .FirstOrDefaultAsync(d => d.StoreId == storeId && d.Name == incoming.Name && !d.IsDeleted, cancellationToken);
        }

        if (device is not null && device.UpdatedAt > incoming.UpdatedAt)
            return device;

        if (device is null)
        {
            device = new Device
            {
                Id = incoming.DeviceId,
                TenantId = await GetTenantIdAsync(db, cancellationToken),
                StoreId = storeId
            };
            db.Devices.Add(device);
        }
        // Never reassign an existing row's Id to match incoming.DeviceId — see the matching comment in
        // POS.Api/Program.cs's copy of this method for why (it would dangle every FK already pointing
        // at that row's original Id). A name-matched row keeps its own Id and just gets its fields
        // refreshed below.

        device.Name = string.IsNullOrWhiteSpace(incoming.Name) ? "Unknown Device" : incoming.Name.Trim();
        device.SyncVersion = incoming.SyncVersion <= 0 ? 1 : incoming.SyncVersion;
        device.CreatedAt = incoming.CreatedAt;
        device.UpdatedAt = incoming.UpdatedAt;
        device.IsDeleted = incoming.IsDeleted;
        device.EnrollmentCodeHash = incoming.EnrollmentCodeHash;
        device.EnrolledAt = incoming.EnrolledAt;
        // Sticky: revocation can never be undone by a generic snapshot upsert — see the matching
        // comment in POS.Api/Program.cs's copy of this method.
        if (incoming.IsRevoked && !device.IsRevoked)
        {
            device.IsRevoked = true;
            device.RevokedAt = incoming.RevokedAt ?? DateTime.UtcNow;
        }
        return device;
    }

    /// <summary>
    /// Resolves a pulled invoice's original actor by username. Returns null — never a fallback to
    /// whichever local user happens to be running sync right now — when a username was supplied but
    /// doesn't (yet) resolve locally, mirroring the server-side fix to the same misattribution class in
    /// <c>/api/sync/invoices/push</c>: a competing claim (the remote username) must never be silently
    /// overridden by the caller's own identity. The caller must skip applying that invoice rather than
    /// misattribute it — it resolves correctly on a later pass once the user syncs down (users pull
    /// before invoices pull in the background service's ordering). Only the no-username case (no
    /// competing claim to override) legitimately falls back to the local session's user.
    /// </summary>
    private async Task<Guid?> ResolveInvoiceUserIdAsync(PosDbContext db, string? username, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
            return _session.UserId;

        return await db.Users
            .AsNoTracking()
            .Where(u => u.StoreId == _session.StoreId && u.Username == username.Trim() && !u.IsDeleted)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Guid?> ResolveAuditLogUserIdAsync(PosDbContext db, string? username, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        return await db.Users
            .AsNoTracking()
            .Where(u => u.StoreId == _session.StoreId && u.Username == username.Trim() && !u.IsDeleted)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<User?> FindLocalUserBySyncIdentityAsync(
        PosDbContext db,
        Guid userId,
        string username,
        CancellationToken cancellationToken)
    {
        var existing = await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId && u.StoreId == _session.StoreId, cancellationToken);

        if (existing is not null)
            return existing;

        return await db.Users
            .FirstOrDefaultAsync(u => u.StoreId == _session.StoreId && u.Username == username.Trim(), cancellationToken);
    }

    private async Task<Guid> EnsureLocalRoleAsync(
        PosDbContext db,
        string roleName,
        int permissionsMask,
        DateTime roleUpdatedAt,
        CancellationToken cancellationToken)
    {
        var tenantId = await GetTenantIdAsync(db, cancellationToken);
        var normalizedRoleName = roleName.Trim();
        var existing = await db.Roles.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == normalizedRoleName, cancellationToken);
        if (existing is not null)
        {
            if (existing.IsDeleted)
                existing.IsDeleted = false;

            // Same UpdatedAt-precedence rule used for every other synced aggregate: only apply the
            // incoming permission mask if it is actually fresher than what's already local.
            if (roleUpdatedAt > existing.UpdatedAt)
            {
                existing.PermissionsMask = permissionsMask;
                existing.UpdatedAt = roleUpdatedAt;
            }

            return existing.Id;
        }

        var role = new Role
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = normalizedRoleName,
            PermissionsMask = permissionsMask,
            CreatedAt = roleUpdatedAt,
            UpdatedAt = roleUpdatedAt,
            IsDeleted = false
        };
        db.Roles.Add(role);
        return role.Id;
    }

    private static void ApplyUserSnapshot(User existing, UserSyncDto incoming, Guid tenantId, Guid roleId, Guid storeId)
    {
        existing.TenantId = tenantId;
        existing.Username = string.IsNullOrWhiteSpace(incoming.Username) ? existing.Username : incoming.Username.Trim();
        existing.NormalizedUsername = existing.Username.Trim().ToUpperInvariant();
        existing.PasswordHash = incoming.PasswordHash;
        existing.RoleId = roleId;
        existing.StoreId = storeId;
        existing.IsActive = incoming.IsActive;
        existing.CreatedAt = incoming.CreatedAt;
        existing.UpdatedAt = incoming.UpdatedAt;
        existing.IsDeleted = incoming.IsDeleted;
    }

    // Every invoice must be keyed by a Box (Device) — when the pulled invoice carries no device
    // identity at all, resolve/create a per-store fallback "Unknown Device" row instead of leaving
    // DeviceId unset, since Invoice.DeviceId is required.
    private async Task<Device> GetOrCreateSyncDeviceAsync(
        PosDbContext db,
        Guid storeId,
        InvoiceSyncInvoiceDto incoming,
        CancellationToken cancellationToken)
    {
        if (incoming.DeviceId is null && string.IsNullOrWhiteSpace(incoming.DeviceName))
            return await GetOrCreateFallbackDeviceAsync(db, storeId, incoming.UpdatedAt, cancellationToken);

        var snapshot = new DeviceSyncDto(
            incoming.DeviceId ?? Guid.NewGuid(),
            string.IsNullOrWhiteSpace(incoming.DeviceName) ? "Unknown Device" : incoming.DeviceName.Trim(),
            1,
            incoming.CreatedAt,
            incoming.UpdatedAt,
            false);

        return await UpsertDeviceSnapshotAsync(db, storeId, snapshot, cancellationToken);
    }

    private async Task<Device> GetOrCreateFallbackDeviceAsync(
        PosDbContext db,
        Guid storeId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        const string fallbackName = "Unknown Device";
        var existing = await db.Devices
            .FirstOrDefaultAsync(d => d.StoreId == storeId && d.Name == fallbackName && !d.IsDeleted, cancellationToken);
        if (existing is not null)
            return existing;

        var created = new Device
        {
            Id = Guid.NewGuid(),
            TenantId = await GetTenantIdAsync(db, cancellationToken),
            StoreId = storeId,
            Name = fallbackName,
            SyncVersion = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Devices.Add(created);
        return created;
    }

    private static Invoice CreateInvoiceFromSync(InvoiceSyncInvoiceDto incoming, Guid tenantId, Guid storeId, Guid userId, Guid deviceId)
    {
        var invoice = new Invoice
        {
            TenantId = tenantId,
            Id = incoming.InvoiceId,
            StoreId = storeId,
            UserId = userId,
            DeviceId = deviceId,
            Status = incoming.Status,
            TotalAmount = incoming.TotalAmount,
            TaxPercent = incoming.TaxPercent,
            Currency = incoming.Currency,
            Notes = incoming.Notes,
            IsSynced = true,
            SyncVersion = incoming.SyncVersion,
            CreatedAt = incoming.CreatedAt,
            UpdatedAt = incoming.UpdatedAt
        };

        foreach (var line in incoming.Lines)
        {
            invoice.Items.Add(new InvoiceItem
            {
                Id = line.LineId,
                InvoiceId = incoming.InvoiceId,
                ProductId = line.ProductId,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                LineTotal = line.LineTotal,
                CreatedAt = line.CreatedAt,
                UpdatedAt = line.UpdatedAt,
                IsDeleted = line.IsDeleted
            });
        }

        foreach (var payment in incoming.Payments)
        {
            invoice.Payments.Add(new Payment
            {
                Id = payment.PaymentId,
                InvoiceId = incoming.InvoiceId,
                Amount = payment.Amount,
                Method = payment.Method,
                PaidAt = payment.PaidAt,
                CreatedAt = payment.CreatedAt,
                UpdatedAt = payment.UpdatedAt,
                IsDeleted = payment.IsDeleted
            });
        }

        return invoice;
    }

    private static void ApplyInvoiceSnapshot(Invoice existing, InvoiceSyncInvoiceDto incoming, Guid userId, Guid deviceId)
    {
        existing.UserId = userId;
        existing.DeviceId = deviceId;
        existing.Status = incoming.Status;
        existing.TotalAmount = incoming.TotalAmount;
        existing.TaxPercent = incoming.TaxPercent;
        existing.Currency = incoming.Currency;
        existing.Notes = incoming.Notes;
        existing.IsSynced = true;
        existing.SyncVersion = incoming.SyncVersion;
        existing.CreatedAt = incoming.CreatedAt;
        existing.UpdatedAt = incoming.UpdatedAt;

        var incomingLines = incoming.Lines.ToDictionary(x => x.LineId);
        foreach (var existingLine in existing.Items)
        {
            if (!incomingLines.TryGetValue(existingLine.Id, out var line))
            {
                existingLine.IsDeleted = true;
                existingLine.UpdatedAt = incoming.UpdatedAt;
                continue;
            }

            ApplyLineSnapshot(existingLine, line);
        }

        foreach (var line in incoming.Lines.Where(x => existing.Items.All(y => y.Id != x.LineId)))
        {
            existing.Items.Add(new InvoiceItem
            {
                Id = line.LineId,
                InvoiceId = existing.Id,
                ProductId = line.ProductId,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                LineTotal = line.LineTotal,
                CreatedAt = line.CreatedAt,
                UpdatedAt = line.UpdatedAt,
                IsDeleted = line.IsDeleted
            });
        }

        var incomingPayments = incoming.Payments.ToDictionary(x => x.PaymentId);
        foreach (var existingPayment in existing.Payments)
        {
            if (!incomingPayments.TryGetValue(existingPayment.Id, out var payment))
            {
                existingPayment.IsDeleted = true;
                existingPayment.UpdatedAt = incoming.UpdatedAt;
                continue;
            }

            ApplyPaymentSnapshot(existingPayment, payment);
        }

        foreach (var payment in incoming.Payments.Where(x => existing.Payments.All(y => y.Id != x.PaymentId)))
        {
            existing.Payments.Add(new Payment
            {
                Id = payment.PaymentId,
                InvoiceId = existing.Id,
                Amount = payment.Amount,
                Method = payment.Method,
                PaidAt = payment.PaidAt,
                CreatedAt = payment.CreatedAt,
                UpdatedAt = payment.UpdatedAt,
                IsDeleted = payment.IsDeleted
            });
        }
    }

    private static void ApplyLineSnapshot(InvoiceItem existingLine, InvoiceSyncLineDto line)
    {
        existingLine.ProductId = line.ProductId;
        existingLine.Quantity = line.Quantity;
        existingLine.UnitPrice = line.UnitPrice;
        existingLine.DiscountPercent = line.DiscountPercent;
        existingLine.LineTotal = line.LineTotal;
        existingLine.CreatedAt = line.CreatedAt;
        existingLine.UpdatedAt = line.UpdatedAt;
        existingLine.IsDeleted = line.IsDeleted;
    }

    private static void ApplyPaymentSnapshot(Payment existingPayment, InvoiceSyncPaymentDto payment)
    {
        existingPayment.Amount = payment.Amount;
        existingPayment.Method = payment.Method;
        existingPayment.PaidAt = payment.PaidAt;
        existingPayment.CreatedAt = payment.CreatedAt;
        existingPayment.UpdatedAt = payment.UpdatedAt;
        existingPayment.IsDeleted = payment.IsDeleted;
    }

    private static async Task ReconcileInvoiceInventoryAsync(
        PosDbContext db,
        Guid tenantId,
        Guid storeId,
        Guid userId,
        Guid invoiceId,
        DateTime changedAt,
        IReadOnlyDictionary<Guid, decimal> previousEffect,
        IReadOnlyDictionary<Guid, decimal> nextEffect,
        CancellationToken cancellationToken)
    {
        var productIds = previousEffect.Keys
            .Concat(nextEffect.Keys)
            .Distinct()
            .ToArray();

        if (productIds.Length == 0)
            return;

        var inventories = await db.Inventories
            .Where(i => i.StoreId == storeId && productIds.Contains(i.ProductId) && !i.IsDeleted)
            .ToDictionaryAsync(i => i.ProductId, cancellationToken);

        foreach (var productId in productIds)
        {
            var previousQuantityDelta = previousEffect.GetValueOrDefault(productId);
            var nextQuantityDelta = nextEffect.GetValueOrDefault(productId);
            var delta = nextQuantityDelta - previousQuantityDelta;
            if (delta == 0)
                continue;

            decimal quantityAfter;
            if (!inventories.TryGetValue(productId, out var inventory))
            {
                // A brand-new inventory row has nothing to race against yet, so a direct set is safe.
                inventory = new Inventory
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ProductId = productId,
                    StoreId = storeId,
                    Quantity = delta,
                    CreatedAt = changedAt,
                    UpdatedAt = changedAt
                };
                db.Inventories.Add(inventory);
                inventories[productId] = inventory;
                quantityAfter = inventory.Quantity;
            }
            else
            {
                // Apply as an atomic DB-level increment, not an in-memory read-modify-write, so two
                // concurrent pulls (e.g. this device applying another device's invoice while its own
                // background sync races) reconciling different invoices for the same product cannot
                // lose one delta to the other. inventory.Quantity is then re-synced from the DB but
                // excluded from this batch's own final SaveChangesAsync, so that later call does not
                // redundantly (and non-atomically) overwrite what this statement already committed.
                await db.Inventories
                    .Where(i => i.Id == inventory.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.Quantity, i => i.Quantity + delta)
                        .SetProperty(i => i.UpdatedAt, changedAt), cancellationToken);

                quantityAfter = await db.Inventories
                    .Where(i => i.Id == inventory.Id)
                    .Select(i => i.Quantity)
                    .FirstAsync(cancellationToken);

                inventory.Quantity = quantityAfter;
                inventory.UpdatedAt = changedAt;
                db.Entry(inventory).Property(i => i.Quantity).IsModified = false;
                db.Entry(inventory).Property(i => i.UpdatedAt).IsModified = false;
            }

            // Central stock going negative here means two offline devices' completed sales could not
            // both be honored against the same physical stock — both sales are preserved and applied
            // exactly once (never discarded, clamped, or overwritten); the shortfall is recorded as a
            // visible discrepancy for manager review instead. Strict global stock availability is not
            // guaranteed while devices are offline — see docs/SYNC_STRATEGY.md.
            var isDiscrepancy = quantityAfter < 0m;

            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = productId,
                StoreId = storeId,
                InventoryId = inventory.Id,
                InvoiceId = invoiceId,
                UserId = userId,
                Type = delta < 0m ? StockMovementType.Sale : StockMovementType.Refund,
                QuantityDelta = delta,
                QuantityAfter = quantityAfter,
                Reference = FormatInvoiceReference(invoiceId),
                Notes = delta < 0m
                    ? "Inventory reduced while reconciling a pulled invoice snapshot."
                    : "Inventory increased while reconciling a pulled invoice snapshot.",
                IsDiscrepancy = isDiscrepancy,
                CreatedAt = changedAt,
                UpdatedAt = changedAt
            });

            if (isDiscrepancy)
            {
                db.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    StoreId = storeId,
                    UserId = userId,
                    Action = "StockDiscrepancyDetected",
                    EntityName = "Product",
                    EntityId = productId,
                    Details = $"Reconciling invoice {FormatInvoiceReference(invoiceId)} drove stock to {quantityAfter:0.####} (below zero). Two or more offline sales likely exceeded available stock before reconnecting.",
                    CreatedAt = changedAt,
                    UpdatedAt = changedAt
                });
            }
        }
    }

    private static IReadOnlyDictionary<Guid, decimal> BuildInventoryEffect(InvoiceStatus status, IEnumerable<InvoiceItem> lines)
    {
        if (status != InvoiceStatus.Paid)
            return new Dictionary<Guid, decimal>();

        return lines
            .Where(line => !line.IsDeleted)
            .GroupBy(line => line.ProductId)
            .ToDictionary(group => group.Key, group => -group.Sum(line => line.Quantity));
    }

    private static string FormatInvoiceReference(Guid invoiceId) => invoiceId.ToString("N")[..12].ToUpperInvariant();

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";

    private sealed record SequenceGuidChange(long Sequence, Guid EntityId);
    private sealed record SequenceKeyChange(long Sequence, string EntityKey);
    private sealed record LoginResponse(string AccessToken);
    private sealed record DeviceTokenRequest(Guid DeviceId, string Secret);
    private sealed record DeviceTokenResponse(string AccessToken, DateTime ExpiresAtUtc);
}