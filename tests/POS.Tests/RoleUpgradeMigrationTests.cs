using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;
using Xunit;

namespace POS.Tests;

/// <summary>
/// T6 upgrade/bootstrap check: an existing tenant's Admin role must gain <see cref="Permission.ManageStores"/>
/// when upgrading to it, via <c>UpgradeAdminRolesManageStores</c>, without expanding any custom role that
/// never held every other permission. Runs the real migration history against a real SQLite file (not
/// <c>EnsureCreated</c>), inserting rows at the exact pre-migration schema state so the migration's own
/// <c>Up</c>/<c>Down</c> SQL is what's under test, not the seeder's separate dev-only fixup.
/// </summary>
public sealed class RoleUpgradeMigrationTests : IAsyncDisposable
{
    private const string PreviousMigration = "20260922092806_InvoiceSyncVersionConcurrencyToken";
    private const int PreManageStoresAll = (int)(Permission.ManageProducts | Permission.ViewReports | Permission.ViewAudit
        | Permission.ManageUsers | Permission.ManageSettings | Permission.ProcessRefunds); // 63
    private const int FullAll = (int)Permission.All; // 127

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pos-role-upgrade-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Upgrade_grants_ManageStores_only_to_roles_that_already_held_every_other_permission()
    {
        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();

        var adminARoleId = Guid.NewGuid();       // tenant A, mask=63 -> must become 127
        var cashierARoleId = Guid.NewGuid();     // tenant A, mask=0  -> must stay 0
        var partialARoleId = Guid.NewGuid();     // tenant A, mask=17 (ManageProducts|ManageSettings) -> must stay 17
        var adminBRoleId = Guid.NewGuid();       // tenant B, mask=63 -> must become 127 (proves all-tenant coverage)
        var alreadyFullBRoleId = Guid.NewGuid(); // tenant B, mask=127 already -> stays 127 (no-op)

        await using (var db = await CreateContextAtAsync(PreviousMigration))
        {
            var now = DateTime.UtcNow;
            db.Tenants.AddRange(
                new Tenant { Id = tenantAId, Name = "Tenant A", NormalizedSlug = "tenant-a", CreatedAt = now, UpdatedAt = now },
                new Tenant { Id = tenantBId, Name = "Tenant B", NormalizedSlug = "tenant-b", CreatedAt = now, UpdatedAt = now });
            db.Roles.AddRange(
                new Role { Id = adminARoleId, TenantId = tenantAId, Name = "Admin", PermissionsMask = PreManageStoresAll, CreatedAt = now, UpdatedAt = now },
                new Role { Id = cashierARoleId, TenantId = tenantAId, Name = "Cashier", PermissionsMask = 0, CreatedAt = now, UpdatedAt = now },
                new Role { Id = partialARoleId, TenantId = tenantAId, Name = "Inventory Clerk", PermissionsMask = (int)(Permission.ManageProducts | Permission.ManageSettings), CreatedAt = now, UpdatedAt = now },
                new Role { Id = adminBRoleId, TenantId = tenantBId, Name = "Admin", PermissionsMask = PreManageStoresAll, CreatedAt = now, UpdatedAt = now },
                new Role { Id = alreadyFullBRoleId, TenantId = tenantBId, Name = "SuperUser", PermissionsMask = FullAll, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        await using (var db = await CreateContextAtAsync(targetMigration: null)) // null = migrate to latest
        {
            var masks = await db.Roles.AsNoTracking()
                .Where(r => r.Id == adminARoleId || r.Id == cashierARoleId || r.Id == partialARoleId || r.Id == adminBRoleId || r.Id == alreadyFullBRoleId)
                .ToDictionaryAsync(r => r.Id, r => r.PermissionsMask);

            Assert.Equal(FullAll, masks[adminARoleId]);        // upgraded
            Assert.Equal(0, masks[cashierARoleId]);            // untouched — never held every other permission
            Assert.Equal(17, masks[partialARoleId]);           // untouched — a real custom/partial role, not expanded
            Assert.Equal(FullAll, masks[adminBRoleId]);        // upgraded — proves the fixup is not scoped to one tenant
            Assert.Equal(FullAll, masks[alreadyFullBRoleId]);  // already full — the OR is a no-op, not a double-grant
        }
    }

    [Fact]
    public async Task Migration_is_reversible_without_throwing()
    {
        var tenantId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        await using (var db = await CreateContextAtAsync(PreviousMigration))
        {
            var now = DateTime.UtcNow;
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", NormalizedSlug = "tenant", CreatedAt = now, UpdatedAt = now });
            db.Roles.Add(new Role { Id = roleId, TenantId = tenantId, Name = "Admin", PermissionsMask = PreManageStoresAll, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        await using (var db = await CreateContextAtAsync(targetMigration: null))
        {
            Assert.Equal(FullAll, await db.Roles.AsNoTracking().Where(r => r.Id == roleId).Select(r => r.PermissionsMask).SingleAsync());
        }

        // Roll back to just before this migration — must not throw, and must revert this exact row.
        await using (var db = await CreateContextAtAsync(PreviousMigration))
        {
            Assert.Equal(PreManageStoresAll, await db.Roles.AsNoTracking().Where(r => r.Id == roleId).Select(r => r.PermissionsMask).SingleAsync());
        }
    }

    private async Task<PosDbContext> CreateContextAtAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var db = new PosDbContext(options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(10); // release the SQLite file handle before deleting
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
