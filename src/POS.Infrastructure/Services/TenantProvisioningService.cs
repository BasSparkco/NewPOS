using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class TenantProvisioningService : ITenantProvisioningService
{
    private readonly IDbContextFactory<PosDbContext> _dbFactory;

    public TenantProvisioningService(IDbContextFactory<PosDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<(bool Success, string? Error, TenantProvisioningResult? Result)> ProvisionTenantAsync(
        string tenantName,
        string tenantSlug,
        string storeName,
        string adminUsername,
        string adminPassword,
        string baseCurrencyCode = "ILS",
        CancellationToken cancellationToken = default)
    {
        tenantName = (tenantName ?? string.Empty).Trim();
        storeName = (storeName ?? string.Empty).Trim();
        adminUsername = (adminUsername ?? string.Empty).Trim();
        var normalizedSlug = (tenantSlug ?? string.Empty).Trim().ToLowerInvariant();
        var currencyCode = string.IsNullOrWhiteSpace(baseCurrencyCode) ? "ILS" : baseCurrencyCode.Trim().ToUpperInvariant();

        if (tenantName.Length == 0)
            return (false, "Tenant name is required.", null);

        if (normalizedSlug.Length == 0)
            return (false, "Tenant slug is required.", null);

        if (storeName.Length == 0)
            return (false, "Store name is required.", null);

        if (adminUsername.Length == 0)
            return (false, "Admin username is required.", null);

        if (string.IsNullOrWhiteSpace(adminPassword) || adminPassword.Length < 8)
            return (false, "Admin password must be at least 8 characters.", null);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var slugExists = await db.Tenants.AnyAsync(t => t.NormalizedSlug == normalizedSlug && !t.IsDeleted, cancellationToken);
        if (slugExists)
            return (false, $"Tenant slug '{normalizedSlug}' is already in use.", null);

        // Currencies are global reference data (unique Code) shared across tenants — reuse the
        // existing row for the requested code rather than inventing per-tenant currency rows. A
        // tenant can change its base currency later via the existing currency-policy screens.
        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.Code == currencyCode && !c.IsDeleted, cancellationToken);
        if (currency is null)
            return (false, $"Currency '{currencyCode}' is not available.", null);

        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = tenantName,
            NormalizedSlug = normalizedSlug,
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Tenants.Add(tenant);

        db.TenantCurrencyRates.Add(new TenantCurrencyRate
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CurrencyId = currency.Id,
            ExchangeRate = 1m,
            CreatedAt = now,
            UpdatedAt = now
        });

        var store = new Store
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = storeName,
            BaseCurrencyId = currency.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Stores.Add(store);

        var adminRole = new Role
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Admin",
            PermissionsMask = (int)Permission.All,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Roles.Add(adminRole);

        var adminUser = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Username = adminUsername,
            NormalizedUsername = adminUsername.ToUpperInvariant(),
            PasswordHash = PasswordHasher.Hash(adminPassword),
            RoleId = adminRole.Id,
            StoreId = store.Id,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Users.Add(adminUser);

        db.UserStoreAccesses.Add(new UserStoreAccess
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserId = adminUser.Id,
            StoreId = store.Id,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        return (true, null, new TenantProvisioningResult(tenant.Id, store.Id, adminUser.Id, adminRole.Id));
    }
}
