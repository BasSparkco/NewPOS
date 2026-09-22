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

    [Fact]
    public async Task Vat_exclusive_adds_tax_on_top_of_subtotal_and_charges_it_on_completion()
    {
        await using var host = await TestServiceHost.CreateAsync();
        await host.OpenDefaultCashSessionAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var settings = services.GetRequiredService<ISettingsService>();
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sales = services.GetRequiredService<ISaleService>();

            await settings.UpdateStoreSettingsAsync(new StoreSettingsDto(false, 5m, 20m, null, PricesIncludeVat: false));

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Exclusive Item",
                Price = 100m,
                Cost = 40m,
                CategoryId = host.CategoryId,
                InitialStock = 10m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();
            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 1m);

            var summary = await sales.GetInvoiceSummaryAsync(invoiceId);
            Assert.False(summary.PricesIncludeVat);
            Assert.Equal(100m, summary.Subtotal);
            Assert.Equal(20m, summary.TaxAmount);
            Assert.Equal(120m, summary.Total);

            var completion = await sales.CompleteCashSaleAsync(invoiceId, 120m);
            Assert.True(completion.Success, completion.ErrorMessage);
            Assert.Equal(120m, completion.Receipt!.Total);
        });
    }

    [Fact]
    public async Task Vat_inclusive_charges_only_the_stored_price_with_no_tax_added()
    {
        await using var host = await TestServiceHost.CreateAsync();
        await host.OpenDefaultCashSessionAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var settings = services.GetRequiredService<ISettingsService>();
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sales = services.GetRequiredService<ISaleService>();

            await settings.UpdateStoreSettingsAsync(new StoreSettingsDto(false, 5m, 20m, null, PricesIncludeVat: true));

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Inclusive Item",
                Price = 120m,
                Cost = 40m,
                CategoryId = host.CategoryId,
                InitialStock = 10m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();
            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 1m);

            var summary = await sales.GetInvoiceSummaryAsync(invoiceId);
            Assert.True(summary.PricesIncludeVat);
            Assert.Equal(120m, summary.Subtotal);
            Assert.Equal(120m, summary.Total); // no tax added on top — the price already includes it
            Assert.Equal(20m, summary.TaxAmount); // informational: the VAT embedded in the price

            var completion = await sales.CompleteCashSaleAsync(invoiceId, 120m);
            Assert.True(completion.Success, completion.ErrorMessage);
            Assert.Equal(120m, completion.Receipt!.Total);
        });
    }
}