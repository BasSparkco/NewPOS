using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class StoreAccessServiceTests
{
    [Fact]
    public async Task Accessible_stores_include_the_users_home_store_without_any_explicit_grant()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var storeAccess = services.GetRequiredService<IStoreAccessService>();
            var accessible = await storeAccess.GetAccessibleStoresAsync();

            var store = Assert.Single(accessible);
            Assert.Equal(host.StoreId, store.Id);
        });
    }

    [Fact]
    public async Task Created_store_inherits_the_tenants_existing_base_currency()
    {
        await using var host = await TestServiceHost.CreateAsync();

        var (success, error, created) = await host.ExecuteScopeAsync(services =>
        {
            var storeAccess = services.GetRequiredService<IStoreAccessService>();
            return storeAccess.CreateStoreAsync("Second Branch", "1 Test Ave", "555-0001");
        });
        Assert.True(success, error);
        Assert.NotNull(created);

        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var store = await db.Stores.AsNoTracking().SingleAsync(s => s.Id == created!.Id);
            Assert.Equal(host.TenantId, store.TenantId);
            Assert.Equal(host.BaseCurrencyId, store.BaseCurrencyId);
        });
    }

    [Fact]
    public async Task Creating_a_store_with_a_duplicate_name_in_the_same_tenant_is_rejected()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var storeAccess = services.GetRequiredService<IStoreAccessService>();
            var (firstSuccess, _, _) = await storeAccess.CreateStoreAsync("Duplicate Branch", null, null);
            Assert.True(firstSuccess);

            var (secondSuccess, error, _) = await storeAccess.CreateStoreAsync("duplicate branch", null, null);
            Assert.False(secondSuccess);
            Assert.NotNull(error);
        });
    }

    [Fact]
    public async Task Granting_then_revoking_store_access_changes_which_stores_a_user_can_switch_into()
    {
        await using var host = await TestServiceHost.CreateAsync();

        Guid branchId = default;
        await host.ExecuteScopeAsync(async services =>
        {
            var storeAccess = services.GetRequiredService<IStoreAccessService>();
            var (success, error, branch) = await storeAccess.CreateStoreAsync("Grantable Branch", null, null);
            Assert.True(success, error);
            branchId = branch!.Id;

            var beforeGrant = await storeAccess.GetAccessibleStoresAsync();
            Assert.DoesNotContain(beforeGrant, s => s.Id == branchId);
            Assert.False(await storeAccess.CanCurrentUserAccessStoreAsync(branchId));

            var (grantSuccess, grantError) = await storeAccess.GrantStoreAccessAsync(host.UserId, branchId);
            Assert.True(grantSuccess, grantError);

            var afterGrant = await storeAccess.GetAccessibleStoresAsync();
            Assert.Contains(afterGrant, s => s.Id == branchId);
            Assert.True(await storeAccess.CanCurrentUserAccessStoreAsync(branchId));

            var (revokeSuccess, revokeError) = await storeAccess.RevokeStoreAccessAsync(host.UserId, branchId);
            Assert.True(revokeSuccess, revokeError);

            var afterRevoke = await storeAccess.GetAccessibleStoresAsync();
            Assert.DoesNotContain(afterRevoke, s => s.Id == branchId);
            Assert.False(await storeAccess.CanCurrentUserAccessStoreAsync(branchId));
        });
    }

    [Fact]
    public async Task Revoking_access_to_a_users_own_home_store_is_rejected()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var storeAccess = services.GetRequiredService<IStoreAccessService>();
            var (success, error) = await storeAccess.RevokeStoreAccessAsync(host.UserId, host.StoreId);

            Assert.False(success);
            Assert.NotNull(error);
            Assert.True(await storeAccess.CanCurrentUserAccessStoreAsync(host.StoreId));
        });
    }

    /// <summary>
    /// T6 matrix row "reference B's product/customer/role/device inside A's valid request → rejected,
    /// including nested and bulk payloads," applied to store access grants: an otherwise well-formed
    /// grant request from tenant A's session must never succeed against a store or a user that actually
    /// belongs to tenant B, even though both ids are individually real, valid rows.
    /// </summary>
    [Fact]
    public async Task Granting_store_access_rejects_a_cross_tenant_store_or_user()
    {
        await using var host = await TestServiceHost.CreateAsync();

        var otherTenantStoreId = Guid.Empty;
        var otherTenantUserId = Guid.Empty;
        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            var now = DateTime.UtcNow;
            var otherTenantId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other Tenant", NormalizedSlug = "other-tenant", Status = TenantStatus.Active, CreatedAt = now, UpdatedAt = now });

            var otherStore = new Store { Id = Guid.NewGuid(), TenantId = otherTenantId, Name = "Other Tenant Store", BaseCurrencyId = host.BaseCurrencyId, CreatedAt = now, UpdatedAt = now };
            db.Stores.Add(otherStore);

            var otherRole = new Role { Id = Guid.NewGuid(), TenantId = otherTenantId, Name = "Admin", PermissionsMask = (int)Permission.All, CreatedAt = now, UpdatedAt = now };
            db.Roles.Add(otherRole);

            var otherUser = new User
            {
                Id = Guid.NewGuid(),
                TenantId = otherTenantId,
                Username = "other.tenant.user",
                NormalizedUsername = "OTHER.TENANT.USER",
                PasswordHash = "hash",
                RoleId = otherRole.Id,
                StoreId = otherStore.Id,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Users.Add(otherUser);

            await db.SaveChangesAsync();
            otherTenantStoreId = otherStore.Id;
            otherTenantUserId = otherUser.Id;
        });

        await host.ExecuteScopeAsync(async services =>
        {
            var storeAccess = services.GetRequiredService<IStoreAccessService>();

            // Real tenant-A user, cross-tenant store.
            var (crossStoreSuccess, crossStoreError) = await storeAccess.GrantStoreAccessAsync(host.UserId, otherTenantStoreId);
            Assert.False(crossStoreSuccess);
            Assert.NotNull(crossStoreError);

            // Cross-tenant user, real tenant-A store.
            var (crossUserSuccess, crossUserError) = await storeAccess.GrantStoreAccessAsync(otherTenantUserId, host.StoreId);
            Assert.False(crossUserSuccess);
            Assert.NotNull(crossUserError);
        });
    }

    /// <summary>
    /// T6 matrix row "guess another tenant's IDs on list/get... → no data disclosure or mutation," applied
    /// to the Stores admin screen's store-selection parameter: a tenant-A admin passing tenant B's store id
    /// (a real, valid row — just not theirs) must fall back to their own store, never render tenant B's row.
    /// </summary>
    [Fact]
    public async Task Tenant_stores_list_never_includes_another_tenants_store_even_by_id()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            var now = DateTime.UtcNow;
            var otherTenantId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other Tenant 2", NormalizedSlug = "other-tenant-2", Status = TenantStatus.Active, CreatedAt = now, UpdatedAt = now });
            db.Stores.Add(new Store { Id = Guid.NewGuid(), TenantId = otherTenantId, Name = "Invisible Store", BaseCurrencyId = host.BaseCurrencyId, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        });

        await host.ExecuteScopeAsync(async services =>
        {
            var storeAccess = services.GetRequiredService<IStoreAccessService>();
            var tenantStores = await storeAccess.GetTenantStoresAsync();
            Assert.DoesNotContain(tenantStores, s => s.Name == "Invisible Store");

            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var invisibleStoreId = await db.Stores.AsNoTracking().Where(s => s.Name == "Invisible Store").Select(s => s.Id).SingleAsync();
            Assert.False(await storeAccess.CanCurrentUserAccessStoreAsync(invisibleStoreId));
        });
    }
}
