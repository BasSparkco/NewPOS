using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class SettingsServiceTests
{
    [Fact]
    public async Task Settings_round_trip_persists_and_loads()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var settings = services.GetRequiredService<ISettingsService>();

            await settings.UpdateStoreSettingsAsync(new StoreSettingsDto(
                AllowNegativeStock: true,
                LowStockThreshold: 12m,
                DefaultTaxPercent: 17m,
                ReceiptFooterText: "Thanks for shopping",
                PricesIncludeVat: true));

            var actual = await settings.GetStoreSettingsAsync();

            Assert.True(actual.AllowNegativeStock);
            Assert.Equal(12m, actual.LowStockThreshold);
            Assert.Equal(17m, actual.DefaultTaxPercent);
            Assert.Equal("Thanks for shopping", actual.ReceiptFooterText);
            Assert.True(actual.PricesIncludeVat);

            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            Assert.Equal(6, await db.Settings.CountAsync(x => x.StoreId == host.StoreId && !x.IsDeleted));
        });
    }

    [Fact]
    public async Task Search_products_uses_configured_low_stock_threshold()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var settings = services.GetRequiredService<ISettingsService>();
            var catalog = services.GetRequiredService<IProductCatalogService>();

            await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Low Stock Item",
                Price = 10m,
                Cost = 5m,
                CategoryId = host.CategoryId,
                InitialStock = 4m,
                IsActive = true
            });

            await settings.UpdateStoreSettingsAsync(new StoreSettingsDto(false, 3m, 0m, null));
            var first = await catalog.SearchProductsAsync("Low Stock Item");
            Assert.False(first.Single().IsLowStock);

            await settings.UpdateStoreSettingsAsync(new StoreSettingsDto(false, 5m, 0m, null));
            var second = await catalog.SearchProductsAsync("Low Stock Item");
            Assert.True(second.Single().IsLowStock);
        });
    }
}