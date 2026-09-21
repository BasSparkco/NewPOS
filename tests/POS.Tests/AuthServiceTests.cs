using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class AuthServiceTests
{
    private const string PasswordA = "PasswordA1!";
    private const string PasswordB = "PasswordB1!";

    [Fact]
    public async Task Same_username_in_two_tenants_authenticates_independently()
    {
        await using var host = await TestServiceHost.CreateAsync();
        var (tenantBId, _, _) = await AddSecondTenantAsync(host, "test-tenant-b", TenantStatus.Active);
        await SetAdminPasswordAsync(host, host.TenantId, PasswordA);

        var loginA = await LoginAsync(host, "admin", PasswordA, "test-tenant");
        Assert.True(loginA.Success, loginA.ErrorMessage);
        Assert.Equal(host.TenantId, host.Session.TenantId);

        var loginB = await LoginAsync(host, "admin", PasswordB, "test-tenant-b");
        Assert.True(loginB.Success, loginB.ErrorMessage);
        Assert.Equal(tenantBId, host.Session.TenantId);
    }

    [Fact]
    public async Task Correct_password_for_wrong_tenant_is_rejected()
    {
        await using var host = await TestServiceHost.CreateAsync();
        await AddSecondTenantAsync(host, "test-tenant-b", TenantStatus.Active);
        await SetAdminPasswordAsync(host, host.TenantId, PasswordA);

        var result = await LoginAsync(host, "admin", PasswordA, "test-tenant-b");

        Assert.False(result.Success);
        Assert.Equal("Invalid username or password.", result.ErrorMessage);
    }

    [Fact]
    public async Task Omitted_slug_fails_once_more_than_one_tenant_exists()
    {
        await using var host = await TestServiceHost.CreateAsync();
        await AddSecondTenantAsync(host, "test-tenant-b", TenantStatus.Active);
        await SetAdminPasswordAsync(host, host.TenantId, PasswordA);

        var result = await LoginAsync(host, "admin", PasswordA, tenantSlug: null);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Unknown_tenant_slug_is_rejected()
    {
        await using var host = await TestServiceHost.CreateAsync();
        await SetAdminPasswordAsync(host, host.TenantId, PasswordA);

        var result = await LoginAsync(host, "admin", PasswordA, "no-such-business");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Suspended_tenant_cannot_authenticate()
    {
        await using var host = await TestServiceHost.CreateAsync();
        await SetAdminPasswordAsync(host, host.TenantId, PasswordA);

        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var tenant = await db.Tenants.SingleAsync(t => t.Id == host.TenantId);
            tenant.Status = TenantStatus.Suspended;
            await db.SaveChangesAsync();
        });

        var result = await LoginAsync(host, "admin", PasswordA, "test-tenant");

        Assert.False(result.Success);
    }

    private static async Task<AuthResult> LoginAsync(TestServiceHost host, string username, string password, string? tenantSlug) =>
        await host.ExecuteScopeAsync(async services =>
        {
            var auth = services.GetRequiredService<IAuthService>();
            return await auth.LoginAsync(username, password, tenantSlug);
        });

    private static async Task SetAdminPasswordAsync(TestServiceHost host, Guid tenantId, string password) =>
        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.SingleAsync(u => u.TenantId == tenantId && u.Username == "admin");
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
            await db.SaveChangesAsync();
        });

    private static async Task<(Guid TenantId, Guid StoreId, Guid UserId)> AddSecondTenantAsync(TestServiceHost host, string slug, TenantStatus status) =>
        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            var now = DateTime.UtcNow;
            var tenantId = Guid.NewGuid();
            var storeId = Guid.NewGuid();
            var roleId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Second Test Tenant",
                NormalizedSlug = slug,
                Status = status,
                CreatedAt = now,
                UpdatedAt = now
            });

            // Currency rows are global reference data (unique Code) — reuse the host's seeded
            // USD currency rather than inserting a duplicate; only the rate row is tenant-scoped.
            db.TenantCurrencyRates.Add(new TenantCurrencyRate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CurrencyId = host.BaseCurrencyId,
                ExchangeRate = 1m,
                CreatedAt = now,
                UpdatedAt = now
            });

            db.Stores.Add(new Store
            {
                Id = storeId,
                TenantId = tenantId,
                Name = "Second Test Store",
                BaseCurrencyId = host.BaseCurrencyId,
                CreatedAt = now,
                UpdatedAt = now
            });

            db.Roles.Add(new Role
            {
                Id = roleId,
                TenantId = tenantId,
                Name = "Admin",
                PermissionsMask = (int)Permission.All,
                CreatedAt = now,
                UpdatedAt = now
            });

            // Same literal username as the first tenant's admin — the point of this fixture is
            // proving that's safe once lookups are scoped by TenantId.
            db.Users.Add(new User
            {
                Id = userId,
                TenantId = tenantId,
                Username = "admin",
                NormalizedUsername = "ADMIN",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(PasswordB),
                RoleId = roleId,
                StoreId = storeId,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });

            await db.SaveChangesAsync();

            return (tenantId, storeId, userId);
        });
}
