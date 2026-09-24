using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using POS.Application.Abstractions;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class BusinessJoinService : IBusinessJoinService
{
    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICurrentSession _session;
    private readonly IInvoiceSyncService _invoiceSyncService;
    private readonly IAuthService _authService;
    private readonly IConfiguration _configuration;

    public BusinessJoinService(
        IDbContextFactory<PosDbContext> dbFactory,
        IHttpClientFactory httpClientFactory,
        ICurrentSession session,
        IInvoiceSyncService invoiceSyncService,
        IAuthService authService,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _httpClientFactory = httpClientFactory;
        _session = session;
        _invoiceSyncService = invoiceSyncService;
        _authService = authService;
        _configuration = configuration;
    }

    public async Task<(bool Success, string? Error)> JoinExistingBusinessAsync(
        string apiBaseUrl,
        string tenantSlug,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        apiBaseUrl = (apiBaseUrl ?? string.Empty).Trim();
        tenantSlug = (tenantSlug ?? string.Empty).Trim();
        username = (username ?? string.Empty).Trim();

        if (apiBaseUrl.Length == 0 || tenantSlug.Length == 0 || username.Length == 0 || string.IsNullOrEmpty(password))
            return (false, "API URL, business slug, username and password are all required.");

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(EnsureTrailingSlash(apiBaseUrl), UriKind.Absolute);

        HttpResponseMessage loginResponse;
        try
        {
            loginResponse = await client.PostAsJsonAsync(
                "api/auth/login",
                new { Username = username, Password = password, TenantSlug = tenantSlug },
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, "Could not reach that server. Check the API URL and your network connection.");
        }

        if (!loginResponse.IsSuccessStatusCode)
            return (false, "Could not sign in to that business. Check the business slug, username and password.");

        var login = await loginResponse.Content.ReadFromJsonAsync<RemoteLoginResponse>(cancellationToken: cancellationToken);
        if (login is null || string.IsNullOrWhiteSpace(login.AccessToken))
            return (false, "The server did not return a valid sign-in response.");

        Guid tenantId;
        int permissionsMask;
        try
        {
            using var payload = DecodeJwtPayload(login.AccessToken);
            tenantId = Guid.Parse(payload.RootElement.GetProperty("tenant_id").GetString()!);
            permissionsMask = int.Parse(payload.RootElement.GetProperty("permissions").GetString()!);
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return (false, "The server's sign-in token was not in the expected format.");
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var storeResponse = await client.GetAsync("api/stores/current", cancellationToken);
        if (!storeResponse.IsSuccessStatusCode)
            return (false, "Signed in, but could not read the store profile from that server.");

        var store = await storeResponse.Content.ReadFromJsonAsync<RemoteStoreResponse>(cancellationToken: cancellationToken);
        if (store is null)
            return (false, "Signed in, but the server did not return a store profile.");

        try
        {
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                // Defensive re-check: never overwrite a device that's already in real use. This also
                // guards the delete below — a fresh install's migrations always insert exactly one
                // placeholder "bootstrap tenant" row (and one TenantCurrencyRate per global Currency)
                // to backfill any pre-existing single-tenant data; on a genuinely fresh install nothing
                // else references it yet, so replacing it here is safe only because no Users exist.
                if (await db.Users.AnyAsync(cancellationToken))
                    return (false, "This device already has local accounts — it isn't a fresh install.");

                var now = DateTime.UtcNow;
                var normalizedSlug = tenantSlug.ToLowerInvariant();

                db.TenantCurrencyRates.RemoveRange(await db.TenantCurrencyRates.ToListAsync(cancellationToken));
                db.Tenants.RemoveRange(await db.Tenants.ToListAsync(cancellationToken));

                db.Tenants.Add(new Tenant
                {
                    Id = tenantId,
                    // No endpoint returns a friendlier tenant display name today (Store profile fields
                    // aren't synced by any aggregate either — a pre-existing gap noted in STATUS.md 4.4),
                    // so the slug the operator typed is the best available local label.
                    Name = tenantSlug,
                    NormalizedSlug = normalizedSlug,
                    Status = TenantStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                });

                db.Stores.Add(new Store
                {
                    Id = login.StoreId,
                    TenantId = tenantId,
                    Name = store.Name,
                    Address = store.Address,
                    Phone = store.Phone,
                    BaseCurrencyId = store.BaseCurrencyId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                });

                // Saved separately from the Setting insert below: mixing this RemoveRange (old
                // bootstrap tenant) with a new tracked Setting in the same SaveChanges call trips EF's
                // relationship-fixup logic ("association ... has been severed") even though the new
                // Setting references the new tenant, not the one being removed.
                await db.SaveChangesAsync(cancellationToken);

                // The background sync worker's own re-login (InvoiceSyncService.TryCreateAuthorizedClientAsync)
                // must supply this same slug — the server can only auto-resolve an omitted slug when exactly
                // one active tenant exists system-wide, which stops being true the moment any second tenant is
                // ever provisioned. Persisted here (store-scoped, matching the DeviceSecret/cursor settings
                // convention) so that later re-login has it without needing an interactive prompt.
                db.Settings.Add(new Setting
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    StoreId = login.StoreId,
                    Key = "Sync.TenantSlug",
                    Value = normalizedSlug,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                });

                await db.SaveChangesAsync(cancellationToken);
            }

            // InvoiceSyncService's pull methods (reused below) each independently read Sync:Enabled and
            // Sync:ApiBaseUrl straight from IConfiguration — the in-memory indexer setter takes effect
            // immediately for the rest of this process (no file, no reload needed) so those calls use
            // the server the operator just typed. WPF's Setup screen additionally persists this to a
            // local config file after a successful join, so future launches keep syncing too.
            _configuration["Sync:Enabled"] = "true";
            _configuration["Sync:ApiBaseUrl"] = apiBaseUrl;

            // ICurrentSession is just an in-memory value holder — populating it here (before any local
            // User row exists) is what lets the existing, already-tested sync pull methods below
            // bootstrap everything else. Order matches InvoiceSyncBackgroundService, minus invoices and
            // audit logs (the regular background sync backfills those from cursor zero once this device
            // starts running normally — no need to duplicate that here).
            _session.Set(tenantId, login.UserId, login.StoreId, login.Username, login.RoleName, permissionsMask, login.BaseCurrencyCode, login.CurrencySymbol);
            _session.SetPassword(password);

            await _invoiceSyncService.PullCurrencyPolicyAsync(cancellationToken);
            await _invoiceSyncService.PullRemoteSettingsAsync(cancellationToken);
            await _invoiceSyncService.PullRemoteUsersAsync(cancellationToken);
            await _invoiceSyncService.PullRemoteCategoriesAsync(cancellationToken);
            await _invoiceSyncService.PullRemoteProductsAsync(cancellationToken);
            await _invoiceSyncService.PullRemoteDevicesAsync(cancellationToken);

            var localLogin = await _authService.LoginAsync(username, password, tenantSlug, cancellationToken);
            if (!localLogin.Success)
                return (false, "Joined the business but could not sign in locally afterward. Please try again.");

            return (true, null);
        }
        catch (Exception)
        {
            await ResetLocalDatabaseAsync(cancellationToken);
            _session.Clear();
            return (false, "Something went wrong while joining that business. No local data was kept — you can try again.");
        }
    }

    /// <summary>
    /// The local database was empty before this attempt by definition (callers only invoke this on a
    /// fresh install) — on any failure partway through, wipe it clean rather than leave a half-seeded
    /// database that the next launch would mistake for an already-configured install.
    /// </summary>
    private async Task ResetLocalDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureDeletedAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";

    private static JsonDocument DecodeJwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
            throw new FormatException("Not a valid JWT.");

        var base64 = parts[1].Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return JsonDocument.Parse(Convert.FromBase64String(base64));
    }

    private sealed record RemoteLoginResponse(
        string AccessToken,
        DateTime ExpiresAtUtc,
        Guid UserId,
        Guid StoreId,
        string Username,
        string RoleName,
        string BaseCurrencyCode,
        string? CurrencySymbol);

    private sealed record RemoteStoreResponse(Guid Id, string Name, string? Address, string? Phone, Guid BaseCurrencyId);
}
