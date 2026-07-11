using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core;
using POS.Core.Entities;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class SyncMetadataTests
{
    [Fact]
    public async Task New_sale_assigns_device_and_initial_sync_metadata()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var sales = services.GetRequiredService<ISaleService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            var invoiceId = await sales.StartNewSaleAsync();

            await using var db = await dbFactory.CreateDbContextAsync();
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == invoiceId);
            var device = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == invoice.DeviceId);

            Assert.False(invoice.IsSynced);
            Assert.Equal(1, invoice.SyncVersion);
            Assert.Equal(host.StoreId, device.StoreId);
            Assert.Equal("POS.Tests", device.Name);
        });
    }

    [Fact]
    public async Task Invoice_mutations_increment_sync_version()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sales = services.GetRequiredService<ISaleService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Sync Item",
                Price = 15m,
                Cost = 6m,
                CategoryId = host.CategoryId,
                InitialStock = 10m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();
            Assert.Equal(1, await GetSyncVersionAsync(dbFactory, invoiceId));

            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 2m);
            Assert.Equal(2, await GetSyncVersionAsync(dbFactory, invoiceId));

            await sales.SetInvoiceTaxAsync(invoiceId, 5m);
            Assert.Equal(3, await GetSyncVersionAsync(dbFactory, invoiceId));

            await using var db = await dbFactory.CreateDbContextAsync();
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == invoiceId);
            Assert.False(invoice.IsSynced);
        });
    }

    [Fact]
    public async Task Tracked_category_save_emits_sync_change_row()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            var categoryId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.Categories.Add(new Category
                {
                    Id = categoryId,
                    Name = "Sequence Category",
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                });

                await db.SaveChangesAsync();
            }

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var change = await verifyDb.SyncChanges
                .AsNoTracking()
                .Where(x => x.AggregateType == SyncAggregateTypes.Category && x.EntityId == categoryId)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();

            Assert.NotNull(change);
            Assert.Null(change!.StoreId);
        });
    }

    [Fact]
    public async Task Tracked_user_save_emits_store_scoped_sync_change_row()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            var now = DateTime.UtcNow;
            Guid userId;
            Guid roleId;

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                roleId = await db.Roles.AsNoTracking()
                    .Where(x => x.Name == "Admin" && !x.IsDeleted)
                    .Select(x => x.Id)
                    .SingleAsync();

                userId = Guid.NewGuid();
                db.Users.Add(new User
                {
                    Id = userId,
                    Username = "sync-user",
                    PasswordHash = "hash",
                    RoleId = roleId,
                    StoreId = host.StoreId,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                });

                await db.SaveChangesAsync();
            }

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var change = await verifyDb.SyncChanges
                .AsNoTracking()
                .Where(x => x.AggregateType == SyncAggregateTypes.User && x.EntityId == userId)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();

            Assert.NotNull(change);
            Assert.Equal(host.StoreId, change!.StoreId);
        });
    }

    [Fact]
    public async Task Sync_cursor_setting_save_does_not_emit_sync_change_row()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            var now = DateTime.UtcNow;

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.Settings.Add(new Setting
                {
                    Id = Guid.NewGuid(),
                    StoreId = host.StoreId,
                    Key = "Sync.InvoicePullSinceVersion",
                    Value = "0",
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                });

                await db.SaveChangesAsync();
            }

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var emitted = await verifyDb.SyncChanges
                .AsNoTracking()
                .AnyAsync(x => x.AggregateType == SyncAggregateTypes.Setting && x.EntityKey == "Sync.InvoicePullSinceVersion");

            Assert.False(emitted);
        });
    }

    private static async Task<int> GetSyncVersionAsync(IDbContextFactory<PosDbContext> dbFactory, Guid invoiceId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Invoices
            .AsNoTracking()
            .Where(x => x.Id == invoiceId)
            .Select(x => x.SyncVersion)
            .SingleAsync();
    }
}