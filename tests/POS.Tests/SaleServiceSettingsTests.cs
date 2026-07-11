using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class SaleServiceSettingsTests
{
    [Fact]
    public async Task Negative_stock_policy_blocks_when_disabled()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var settings = services.GetRequiredService<ISettingsService>();
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sales = services.GetRequiredService<ISaleService>();

            await settings.UpdateStoreSettingsAsync(new StoreSettingsDto(false, 5m, 0m, null));

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Limited Item",
                Price = 20m,
                Cost = 8m,
                CategoryId = host.CategoryId,
                InitialStock = 2m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sales.AddOrMergeLineAsync(invoiceId, product.Id, 3m));
        });
    }

    [Fact]
    public async Task Negative_stock_policy_allows_oversell_and_uses_default_tax()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var settings = services.GetRequiredService<ISettingsService>();
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sales = services.GetRequiredService<ISaleService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            await settings.UpdateStoreSettingsAsync(new StoreSettingsDto(true, 5m, 17m, null));

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Oversell Item",
                Price = 20m,
                Cost = 8m,
                CategoryId = host.CategoryId,
                InitialStock = 2m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();
            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 3m);

            var lines = await sales.GetCartLinesAsync(invoiceId);
            Assert.Single(lines);
            Assert.Equal(3m, lines[0].Quantity);

            await using var db = await dbFactory.CreateDbContextAsync();
            var invoice = await db.Invoices.FirstAsync(x => x.Id == invoiceId);
            Assert.Equal(17m, invoice.TaxPercent);
        });
    }
}