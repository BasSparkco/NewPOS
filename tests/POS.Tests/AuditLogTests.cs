using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;

namespace POS.Tests;

public class AuditLogTests
{
    [Fact]
    public async Task Currency_policy_update_writes_audit_row()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var currencies = services.GetRequiredService<ICurrencyService>();
            var audit = services.GetRequiredService<IAuditLogService>();

            await currencies.UpdateStoreCurrencyPolicyAsync(
                host.AltCurrencyId,
                new[]
                {
                    new CurrencyRateUpdateDto(host.BaseCurrencyId, 1m),
                    new CurrencyRateUpdateDto(host.AltCurrencyId, 1m)
                });

            var rows = await audit.GetRecentAsync(action: "CurrencyPolicyUpdated", take: 20);

            var row = Assert.Single(rows);
            Assert.Equal("Store", row.EntityName);
            Assert.Contains("USD -> EUR", row.Details);
        });
    }

    [Fact]
    public async Task Product_update_writes_product_and_manual_stock_audits()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var audit = services.GetRequiredService<IAuditLogService>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Audit Product",
                Price = 5m,
                Cost = 2m,
                CategoryId = host.CategoryId,
                InitialStock = 4m,
                IsActive = true
            });

            product.Name = "Audit Product Updated";
            product.Price = 7m;
            product.InitialStock = 9m;

            await catalog.UpdateProductAsync(product);

            var rows = await audit.GetRecentAsync(take: 50);
            Assert.Contains(rows, x => x.Action == "ProductUpdated" && x.EntityId == product.Id);
            Assert.Contains(rows, x => x.Action == "ManualStockAdjusted" && x.Details != null && x.Details.Contains("4"));
        });
    }

    [Fact]
    public async Task Refund_writes_invoice_refunded_audit_row()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sales = services.GetRequiredService<ISaleService>();
            var audit = services.GetRequiredService<IAuditLogService>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Refundable Item",
                Price = 25m,
                Cost = 10m,
                CategoryId = host.CategoryId,
                InitialStock = 10m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();
            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 2m);

            var completion = await sales.CompleteCashSaleAsync(invoiceId, 50m);
            Assert.True(completion.Success, completion.ErrorMessage);

            var refund = await sales.RefundInvoiceAsync(invoiceId);
            Assert.True(refund.Success, refund.Error);

            var rows = await audit.GetRecentAsync(action: "InvoiceRefunded", take: 20);
            var row = Assert.Single(rows);
            Assert.Equal(invoiceId, row.EntityId);
            Assert.Contains("restored", row.Details);
        });
    }
}