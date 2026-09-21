using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Models;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class ApiIntegrationTests
{
    [Fact]
    public async Task Login_endpoint_rate_limits_repeated_attempts_from_the_same_client()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        // Policy allows 10 requests per minute per client IP (tenant.md's identity section: throttle
        // login to slow brute-force/enumeration). All requests share the test server's loopback address,
        // so the 11th request in this single window must be rejected regardless of the credentials sent.
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", $"wrong-password-{attempt}"));
        }

        Assert.NotEqual(HttpStatusCode.TooManyRequests, response!.StatusCode);

        var eleventh = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "wrong-password-11"));
        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
    }

    [Fact]
    public async Task Login_and_catalog_endpoints_return_seeded_data()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);

        var me = await client.GetFromJsonAsync<CurrentUserResponse>("/api/auth/me");
        Assert.NotNull(me);
        Assert.Equal("admin", me.Username);
        Assert.Equal("Admin", me.RoleName);

        var categories = await client.GetFromJsonAsync<List<CategoryResponse>>("/api/catalog/categories");
        Assert.NotNull(categories);
        Assert.Contains(categories, c => c.Name == "General");

        var products = await client.GetFromJsonAsync<List<ProductResponse>>("/api/catalog/products?query=Sample");
        Assert.NotNull(products);
        Assert.Contains(products, p => p.Name == "Sample Item A");
    }

    [Fact]
    public async Task Cash_sale_flow_returns_updated_snapshots_and_receipt()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var product = await GetProductAsync(client, "Sample Item A");

        var opened = await PostAndReadAsync<SaleSnapshotResponse>(client, "/api/sales/open", new { });
        Assert.NotEqual(Guid.Empty, opened.InvoiceId);
        Assert.Empty(opened.Lines);

        var afterAdd = await PostAndReadAsync<SaleSnapshotResponse>(
            client,
            $"/api/sales/{opened.InvoiceId}/lines",
            new AddSaleLineRequest(product.Id, 2m));

        var line = Assert.Single(afterAdd.Lines);
        Assert.Equal(product.Id, line.ProductId);
        Assert.Equal(2m, line.Quantity);

        var afterQuantity = await PatchAndReadAsync<SaleSnapshotResponse>(
            client,
            $"/api/sales/{opened.InvoiceId}/lines/{line.LineId}/quantity",
            new UpdateSaleLineQuantityRequest(3m));

        var afterDiscount = await PatchAndReadAsync<SaleSnapshotResponse>(
            client,
            $"/api/sales/{opened.InvoiceId}/lines/{line.LineId}/discount",
            new UpdateSaleLineDiscountRequest(10m));

        var afterTax = await PatchAndReadAsync<SaleSnapshotResponse>(
            client,
            $"/api/sales/{opened.InvoiceId}/tax",
            new UpdateSaleTaxRequest(5m));

        Assert.Equal(3m, Assert.Single(afterQuantity.Lines).Quantity);
        Assert.Equal(10m, afterDiscount.Lines.Single().DiscountPercent);
        Assert.Equal(5m, afterTax.Summary.TaxPercent);
        Assert.True(afterTax.Summary.Total > 0m);

        var completion = await PostAndReadAsync<CompleteCashSaleResponse>(
            client,
            $"/api/sales/{opened.InvoiceId}/complete/cash",
            new CompleteCashSaleRequest(afterTax.Summary.Total + 10m));

        Assert.NotNull(completion.Receipt);
        Assert.Equal("Sample Item A", Assert.Single(completion.Receipt.Lines).Name);
        Assert.True(completion.Receipt.Change > 0m);
    }

    [Fact]
    public async Task Lifecycle_endpoints_transition_invoice_status_and_allow_refund_after_payment()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var product = await GetProductAsync(client, "Sample Item B");

        var firstSale = await PostAndReadAsync<SaleSnapshotResponse>(client, "/api/sales/open", new { });
        await PostAndReadAsync<SaleSnapshotResponse>(
            client,
            $"/api/sales/{firstSale.InvoiceId}/lines",
            new AddSaleLineRequest(product.Id, 1m));

        var held = await PostAndReadAsync<SaleLifecycleResponse>(client, $"/api/sales/{firstSale.InvoiceId}/hold", new { });
        Assert.Equal("Held", held.Status);

        var resumed = await PostAndReadAsync<SaleLifecycleResponse>(client, $"/api/sales/{firstSale.InvoiceId}/resume", new { });
        Assert.Equal("Open", resumed.Status);

        var cancelled = await PostAndReadAsync<SaleLifecycleResponse>(client, $"/api/sales/{firstSale.InvoiceId}/cancel", new { });
        Assert.Equal("Cancelled", cancelled.Status);

        var refundAttempt = await client.PostAsJsonAsync($"/api/sales/{firstSale.InvoiceId}/refund", new { });
        Assert.Equal(HttpStatusCode.BadRequest, refundAttempt.StatusCode);

        var secondSale = await PostAndReadAsync<SaleSnapshotResponse>(client, "/api/sales/open", new { });
        var afterAdd = await PostAndReadAsync<SaleSnapshotResponse>(
            client,
            $"/api/sales/{secondSale.InvoiceId}/lines",
            new AddSaleLineRequest(product.Id, 2m));

        var paid = await PostAndReadAsync<CompleteCashSaleResponse>(
            client,
            $"/api/sales/{secondSale.InvoiceId}/complete/cash",
            new CompleteCashSaleRequest(afterAdd.Summary.Total + 5m));
        Assert.True(paid.Receipt.Total > 0m);

        var refunded = await PostAndReadAsync<SaleLifecycleResponse>(client, $"/api/sales/{secondSale.InvoiceId}/refund", new { });
        Assert.Equal("Cancelled", refunded.Status);
    }

    [Fact]
    public async Task Refund_endpoint_rejects_a_user_without_ProcessRefunds_permission()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var product = await GetProductAsync(client, "Sample Item A");

        var sale = await PostAndReadAsync<SaleSnapshotResponse>(client, "/api/sales/open", new { });
        var afterAdd = await PostAndReadAsync<SaleSnapshotResponse>(
            client,
            $"/api/sales/{sale.InvoiceId}/lines",
            new AddSaleLineRequest(product.Id, 1m));

        await PostAndReadAsync<CompleteCashSaleResponse>(
            client,
            $"/api/sales/{sale.InvoiceId}/complete/cash",
            new CompleteCashSaleRequest(afterAdd.Summary.Total + 5m));

        // Cashier's seeded PermissionsMask is Permission.None — no ProcessRefunds.
        const string cashierPassword = "NoRefundCashier1!";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var storeId = await db.Stores.AsNoTracking().Select(s => s.Id).SingleAsync();
            var tenantId = await db.Stores.AsNoTracking().Select(s => s.TenantId).SingleAsync();
            var cashierRoleId = await db.Roles.AsNoTracking().Where(r => r.Name == "Cashier").Select(r => r.Id).SingleAsync();

            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Username = "no.refund.cashier",
                NormalizedUsername = "NO.REFUND.CASHIER",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(cashierPassword),
                RoleId = cashierRoleId,
                StoreId = storeId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsDeleted = false
            });
            await db.SaveChangesAsync();
        }

        using var cashierClient = factory.CreateClient();
        var login = await PostAndReadAsync<LoginResponse>(
            cashierClient,
            "/api/auth/login",
            new LoginRequest("no.refund.cashier", cashierPassword));
        cashierClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var refundAttempt = await cashierClient.PostAsJsonAsync($"/api/sales/{sale.InvoiceId}/refund", new { });
        Assert.Equal(HttpStatusCode.Forbidden, refundAttempt.StatusCode);
    }

    [Fact]
    public async Task Sync_push_endpoints_reject_a_user_without_the_matching_management_permission()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);

        // Cashier's seeded PermissionsMask is Permission.None — no ManageUsers/ManageSettings/ManageProducts.
        // A device signed in as this cashier re-authenticates with these exact credentials to push pending
        // local sync changes (InvoiceSyncService.TryCreateAuthorizedClientAsync), so this proves a Cashier's
        // session cannot use that channel to smuggle in a privilege escalation, product price tamper, or
        // settings/device change — closing the gap tenant.md's T2 milestone calls out.
        const string cashierPassword = "NoManagePermsCashier1!";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var storeId = await db.Stores.AsNoTracking().Select(s => s.Id).SingleAsync();
            var tenantId = await db.Stores.AsNoTracking().Select(s => s.TenantId).SingleAsync();
            var cashierRoleId = await db.Roles.AsNoTracking().Where(r => r.Name == "Cashier").Select(r => r.Id).SingleAsync();

            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Username = "no.manage.cashier",
                NormalizedUsername = "NO.MANAGE.CASHIER",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(cashierPassword),
                RoleId = cashierRoleId,
                StoreId = storeId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsDeleted = false
            });
            await db.SaveChangesAsync();
        }

        using var cashierClient = factory.CreateClient();
        var login = await PostAndReadAsync<LoginResponse>(
            cashierClient,
            "/api/auth/login",
            new LoginRequest("no.manage.cashier", cashierPassword));
        cashierClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var now = DateTime.UtcNow;

        var usersAttempt = await cashierClient.PostAsJsonAsync("/api/sync/users/push", new UserSyncBatchRequest(
        [new UserSyncRequest(Guid.NewGuid(), "smuggled.admin", "hash", "Admin", true, now, now, false)]));
        Assert.Equal(HttpStatusCode.Forbidden, usersAttempt.StatusCode);

        var settingsAttempt = await cashierClient.PostAsJsonAsync("/api/sync/settings/push", new SettingsSyncBatchRequest(
        [new SettingSyncRequest("ReceiptFooterText", "Tampered footer", now, now, false)]));
        Assert.Equal(HttpStatusCode.Forbidden, settingsAttempt.StatusCode);

        var productsAttempt = await cashierClient.PostAsJsonAsync("/api/sync/products/push", new ProductSyncBatchRequest(
        [new ProductSyncRequest(Guid.NewGuid(), "Tampered Product", "99999", 0.01m, 0m, Guid.NewGuid(), "Tampered Category", 999m, true, null, now, now, false)]));
        Assert.Equal(HttpStatusCode.Forbidden, productsAttempt.StatusCode);

        var categoriesAttempt = await cashierClient.PostAsJsonAsync("/api/sync/categories/push", new CategorySyncBatchRequest(
        [new CategorySyncRequest(Guid.NewGuid(), "Tampered Category", now, now, false)]));
        Assert.Equal(HttpStatusCode.Forbidden, categoriesAttempt.StatusCode);

        var devicesAttempt = await cashierClient.PostAsJsonAsync("/api/sync/devices/push", new DeviceSyncBatchRequest(
        [new DeviceSyncRequest(Guid.NewGuid(), "Tampered Register", 1, now, now, false)]));
        Assert.Equal(HttpStatusCode.Forbidden, devicesAttempt.StatusCode);

        await using var currencyScope = factory.Services.CreateAsyncScope();
        var currencyDbFactory = currencyScope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var currencyDb = await currencyDbFactory.CreateDbContextAsync();
        var store = await currencyDb.Stores.AsNoTracking().SingleAsync();
        var currencies = await currencyDb.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();

        var currencyAttempt = await cashierClient.PostAsJsonAsync("/api/sync/currency-policy/push", new CurrencyPolicySyncDto(
            store.Id,
            store.BaseCurrencyId,
            now,
            currencies.Select(c => new CurrencyDto(c.Id, c.Code, c.Name, c.Symbol, 1m)).ToList()));
        Assert.Equal(HttpStatusCode.Forbidden, currencyAttempt.StatusCode);
    }

    [Fact]
    public async Task Sync_endpoint_applies_invoice_snapshot_and_reports_conflict_for_stale_version()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var product = await GetProductAsync(client, "Sample Item A");

        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var firstPush = await PostAndReadAsync<InvoiceSyncPushResultResponse>(client, "/api/sync/invoices/push", new InvoiceSyncBatchRequest(
        [
            new InvoiceSyncInvoiceRequest(
                invoiceId,
                deviceId,
                "API Sync Device",
                3,
                1,
                21m,
                5m,
                "USD",
                null,
                now,
                now,
                [new InvoiceSyncLineRequest(lineId, product.Id, 2m, 10m, 0m, 20m, now, now, false)],
                [new InvoiceSyncPaymentRequest(paymentId, 21m, 0, now, now, now, false)])]));

        var firstResult = Assert.Single(firstPush.Results);
        Assert.True(string.Equals(firstResult.Status, "Applied", StringComparison.Ordinal), firstResult.ErrorMessage);
        Assert.Equal(3, firstResult.ServerSyncVersion);

        var syncedSale = await client.GetFromJsonAsync<SaleSnapshotResponse>($"/api/sales/{invoiceId}");
        Assert.NotNull(syncedSale);
        Assert.Equal(21m, syncedSale.Summary.Total);
        Assert.Single(syncedSale.Lines);

        var stalePush = await PostAndReadAsync<InvoiceSyncPushResultResponse>(client, "/api/sync/invoices/push", new InvoiceSyncBatchRequest(
        [
            new InvoiceSyncInvoiceRequest(
                invoiceId,
                deviceId,
                "API Sync Device",
                2,
                1,
                21m,
                5m,
                "USD",
                null,
                now,
                now,
                [new InvoiceSyncLineRequest(lineId, product.Id, 2m, 10m, 0m, 20m, now, now, false)],
                [new InvoiceSyncPaymentRequest(paymentId, 21m, 0, now, now, now, false)])]));

        var staleResult = Assert.Single(stalePush.Results);
        Assert.Equal("Conflict", staleResult.Status);
        Assert.Equal(3, staleResult.ServerSyncVersion);
    }

    [Fact]
    public async Task Sync_endpoint_reconciles_inventory_when_paid_invoice_is_later_refunded()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var product = await GetProductAsync(client, "Sample Item A");
        var startingQuantity = product.QuantityOnHand;

        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var paidAt = DateTime.UtcNow;
        var refundedAt = paidAt.AddMinutes(5);

        var paidPush = await PostAndReadAsync<InvoiceSyncPushResultResponse>(client, "/api/sync/invoices/push", new InvoiceSyncBatchRequest(
        [
            new InvoiceSyncInvoiceRequest(
                invoiceId,
                deviceId,
                "API Sync Device",
                3,
                (int)InvoiceStatus.Paid,
                21m,
                5m,
                "USD",
                null,
                paidAt,
                paidAt,
                [new InvoiceSyncLineRequest(lineId, product.Id, 2m, 10m, 0m, 20m, paidAt, paidAt, false)],
                [new InvoiceSyncPaymentRequest(paymentId, 21m, 0, paidAt, paidAt, paidAt, false)])]));

        Assert.Equal("Applied", Assert.Single(paidPush.Results).Status);

        var afterPaid = await GetProductAsync(client, "Sample Item A");
        Assert.Equal(startingQuantity - 2m, afterPaid.QuantityOnHand);

        var refundedPush = await PostAndReadAsync<InvoiceSyncPushResultResponse>(client, "/api/sync/invoices/push", new InvoiceSyncBatchRequest(
        [
            new InvoiceSyncInvoiceRequest(
                invoiceId,
                deviceId,
                "API Sync Device",
                4,
                (int)InvoiceStatus.Cancelled,
                21m,
                5m,
                "USD",
                null,
                paidAt,
                refundedAt,
                [new InvoiceSyncLineRequest(lineId, product.Id, 2m, 10m, 0m, 20m, paidAt, refundedAt, false)],
                [new InvoiceSyncPaymentRequest(paymentId, 21m, 0, paidAt, paidAt, refundedAt, false)])]));

        Assert.Equal("Applied", Assert.Single(refundedPush.Results).Status);

        var afterRefund = await GetProductAsync(client, "Sample Item A");
        Assert.Equal(startingQuantity, afterRefund.QuantityOnHand);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var movements = await db.StockMovements
            .AsNoTracking()
            .Where(m => m.InvoiceId == invoiceId && m.ProductId == product.Id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        Assert.Collection(
            movements,
            movement =>
            {
                Assert.Equal(StockMovementType.Sale, movement.Type);
                Assert.Equal(-2m, movement.QuantityDelta);
                Assert.Equal(startingQuantity - 2m, movement.QuantityAfter);
            },
            movement =>
            {
                Assert.Equal(StockMovementType.Refund, movement.Type);
                Assert.Equal(2m, movement.QuantityDelta);
                Assert.Equal(startingQuantity, movement.QuantityAfter);
            });
    }

    [Fact]
    public async Task Sync_pull_endpoint_returns_server_invoice_changes_after_cursor()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var product = await GetProductAsync(client, "Sample Item B");

        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await PostAndReadAsync<InvoiceSyncPushResultResponse>(client, "/api/sync/invoices/push", new InvoiceSyncBatchRequest(
        [
            new InvoiceSyncInvoiceRequest(
                invoiceId,
                deviceId,
                "API Sync Device",
                3,
                (int)POS.Core.Enums.InvoiceStatus.Paid,
                9m,
                0m,
                "USD",
                null,
                now,
                now,
                [new InvoiceSyncLineRequest(lineId, product.Id, 2m, 4.5m, 0m, 9m, now, now, false)],
                [new InvoiceSyncPaymentRequest(paymentId, 9m, 0, now, now, now, false)])]));

        var firstPull = await GetAndReadAsync<InvoiceSyncPullResponse>(client, "/api/sync/invoices/pull?sinceVersion=0:00000000000000000000000000000000&batchSize=10");
        var pulledInvoice = Assert.Single(firstPull.Invoices);
        Assert.Equal(invoiceId, pulledInvoice.InvoiceId);
        Assert.Equal(3, pulledInvoice.SyncVersion);
        Assert.Equal("admin", pulledInvoice.Username);
        Assert.Equal(1, pulledInvoice.Status);
        Assert.False(string.IsNullOrWhiteSpace(firstPull.NextSinceVersion));

        var secondPull = await GetAndReadAsync<InvoiceSyncPullResponse>(
            client,
            $"/api/sync/invoices/pull?sinceVersion={Uri.EscapeDataString(firstPull.NextSinceVersion)}&batchSize=10");

        Assert.Empty(secondPull.Invoices);
        Assert.Equal(firstPull.NextSinceVersion, secondPull.NextSinceVersion);
    }

    [Fact]
    public async Task Sync_category_pull_endpoint_returns_server_category_changes_after_cursor()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var baseline = await GetAndReadAsync<CategorySyncPullResponse>(
            client,
            "/api/sync/categories/pull?sinceVersion=0:00000000000000000000000000000000&batchSize=200");

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        var categoryId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var tenantId = await db.Stores.AsNoTracking().Select(s => s.TenantId).SingleAsync();

            db.Categories.Add(new Category
            {
                Id = categoryId,
                TenantId = tenantId,
                Name = "Server Synced Category",
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            await db.SaveChangesAsync();
        }

        var delta = await GetAndReadAsync<CategorySyncPullResponse>(
            client,
            $"/api/sync/categories/pull?sinceVersion={Uri.EscapeDataString(baseline.NextSinceVersion)}&batchSize=20");

        var pulled = Assert.Single(delta.Categories);
        Assert.Equal(categoryId, pulled.CategoryId);
        Assert.Equal("Server Synced Category", pulled.Name);
        Assert.False(string.IsNullOrWhiteSpace(delta.NextSinceVersion));

        var secondPull = await GetAndReadAsync<CategorySyncPullResponse>(
            client,
            $"/api/sync/categories/pull?sinceVersion={Uri.EscapeDataString(delta.NextSinceVersion)}&batchSize=20");

        Assert.Empty(secondPull.Categories);
        Assert.Equal(delta.NextSinceVersion, secondPull.NextSinceVersion);
    }

    [Fact]
    public async Task Sync_product_pull_endpoint_returns_server_product_changes_after_cursor()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var baseline = await GetAndReadAsync<ProductSyncPullResponse>(client, "/api/sync/products/pull?sinceVersion=0:00000000000000000000000000000000&batchSize=200");

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var storeId = await db.Stores.AsNoTracking().Select(s => s.Id).SingleAsync();
            var tenantId = await db.Stores.AsNoTracking().Select(s => s.TenantId).SingleAsync();
            var now = DateTime.UtcNow;
            var categoryId = Guid.NewGuid();
            var productId = Guid.NewGuid();

            db.Categories.Add(new Category
            {
                Id = categoryId,
                TenantId = tenantId,
                Name = "Synced Category",
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });
            db.Products.Add(new Product
            {
                Id = productId,
                TenantId = tenantId,
                Name = "Server Synced Product",
                Barcode = "30001",
                Price = 7.5m,
                Cost = 3m,
                CategoryId = categoryId,
                IsWeighted = false,
                IsActive = true,
                ImagePath = null,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });
            db.Inventories.Add(new Inventory
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = productId,
                StoreId = storeId,
                Quantity = 14m,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            await db.SaveChangesAsync();
        }

        var delta = await GetAndReadAsync<ProductSyncPullResponse>(
            client,
            $"/api/sync/products/pull?sinceVersion={Uri.EscapeDataString(baseline.NextSinceVersion)}&batchSize=20");

        var pulled = Assert.Single(delta.Products);
        Assert.Equal("Server Synced Product", pulled.Name);
        Assert.Equal("Synced Category", pulled.CategoryName);
        Assert.Equal(14m, pulled.QuantityOnHand);
        Assert.True(pulled.IsActive);
        Assert.False(string.IsNullOrWhiteSpace(delta.NextSinceVersion));

        var secondPull = await GetAndReadAsync<ProductSyncPullResponse>(
            client,
            $"/api/sync/products/pull?sinceVersion={Uri.EscapeDataString(delta.NextSinceVersion)}&batchSize=20");

        Assert.Empty(secondPull.Products);
        Assert.Equal(delta.NextSinceVersion, secondPull.NextSinceVersion);
    }

    [Fact]
    public async Task Sync_settings_pull_endpoint_returns_server_setting_changes_after_cursor()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var baseline = await GetAndReadAsync<SettingsSyncPullResponse>(client, "/api/sync/settings/pull?sinceVersion=0:&batchSize=200");

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var storeId = await db.Stores.AsNoTracking().Select(s => s.Id).SingleAsync();
            var tenantId = await db.Stores.AsNoTracking().Select(s => s.TenantId).SingleAsync();
            var now = DateTime.UtcNow;

            db.Settings.Add(new Setting
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StoreId = storeId,
                Key = "ReceiptFooterText",
                Value = "Synced footer",
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            await db.SaveChangesAsync();
        }

        var delta = await GetAndReadAsync<SettingsSyncPullResponse>(
            client,
            $"/api/sync/settings/pull?sinceVersion={Uri.EscapeDataString(baseline.NextSinceVersion)}&batchSize=20");

        var pulled = Assert.Single(delta.Settings);
        Assert.Equal("ReceiptFooterText", pulled.Key);
        Assert.Equal("Synced footer", pulled.Value);
        Assert.False(pulled.IsDeleted);

        var secondPull = await GetAndReadAsync<SettingsSyncPullResponse>(
            client,
            $"/api/sync/settings/pull?sinceVersion={Uri.EscapeDataString(delta.NextSinceVersion)}&batchSize=20");

        Assert.Empty(secondPull.Settings);
        Assert.Equal(delta.NextSinceVersion, secondPull.NextSinceVersion);
    }

    [Fact]
    public async Task Sync_users_pull_endpoint_returns_server_user_changes_after_cursor()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var baseline = await GetAndReadAsync<UserSyncPullResponse>(client, "/api/sync/users/pull?sinceVersion=0&batchSize=200");

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var storeId = await db.Stores.AsNoTracking().Select(s => s.Id).SingleAsync();
            var tenantId = await db.Stores.AsNoTracking().Select(s => s.TenantId).SingleAsync();
            var cashierRoleId = await db.Roles.AsNoTracking().Where(r => r.Name == "Cashier").Select(r => r.Id).SingleAsync();

            db.Users.Add(new User
            {
                Id = userId,
                TenantId = tenantId,
                Username = "server.user",
                NormalizedUsername = "SERVER.USER",
                PasswordHash = "server-hash",
                RoleId = cashierRoleId,
                StoreId = storeId,
                IsActive = false,
                CreatedAt = now.AddMinutes(-5),
                UpdatedAt = now,
                IsDeleted = false
            });

            await db.SaveChangesAsync();
        }

        var delta = await GetAndReadAsync<UserSyncPullResponse>(
            client,
            $"/api/sync/users/pull?sinceVersion={Uri.EscapeDataString(baseline.NextSinceVersion)}&batchSize=20");

        var pulled = Assert.Single(delta.Users);
        Assert.Equal(userId, pulled.UserId);
        Assert.Equal("server.user", pulled.Username);
        Assert.Equal("server-hash", pulled.PasswordHash);
        Assert.Equal("Cashier", pulled.RoleName);
        Assert.False(pulled.IsActive);

        var secondPull = await GetAndReadAsync<UserSyncPullResponse>(
            client,
            $"/api/sync/users/pull?sinceVersion={Uri.EscapeDataString(delta.NextSinceVersion)}&batchSize=20");

        Assert.Empty(secondPull.Users);
        Assert.Equal(delta.NextSinceVersion, secondPull.NextSinceVersion);
    }

    [Fact]
    public async Task Sync_audit_logs_pull_endpoint_returns_server_audit_changes_after_cursor()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var baseline = await GetAndReadAsync<AuditLogSyncPullResponse>(client, "/api/sync/audit-logs/pull?sinceVersion=0:&batchSize=200");

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var storeId = await db.Stores.AsNoTracking().Select(s => s.Id).SingleAsync();
            var tenantId = await db.Stores.AsNoTracking().Select(s => s.TenantId).SingleAsync();
            var userId = await db.Users.AsNoTracking().Where(u => u.Username == "admin").Select(u => u.Id).SingleAsync();
            var now = DateTime.UtcNow;

            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StoreId = storeId,
                UserId = userId,
                Action = "ProductUpdated",
                EntityName = "Product",
                EntityId = Guid.NewGuid(),
                Details = "Pulled audit row",
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            await db.SaveChangesAsync();
        }

        var delta = await GetAndReadAsync<AuditLogSyncPullResponse>(
            client,
            $"/api/sync/audit-logs/pull?sinceVersion={Uri.EscapeDataString(baseline.NextSinceVersion)}&batchSize=20");

        var pulled = Assert.Single(delta.AuditLogs);
        Assert.Equal("ProductUpdated", pulled.Action);
        Assert.Equal("Product", pulled.EntityName);
        Assert.Equal("Pulled audit row", pulled.Details);
        Assert.Equal("admin", pulled.Username);

        var secondPull = await GetAndReadAsync<AuditLogSyncPullResponse>(
            client,
            $"/api/sync/audit-logs/pull?sinceVersion={Uri.EscapeDataString(delta.NextSinceVersion)}&batchSize=20");

        Assert.Empty(secondPull.AuditLogs);
        Assert.Equal(delta.NextSinceVersion, secondPull.NextSinceVersion);
    }

    [Fact]
    public async Task Sync_audit_logs_push_endpoint_applies_snapshot_on_server()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var auditLogId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var response = await PostAndReadAsync<AuditLogSyncPushResponse>(client, "/api/sync/audit-logs/push", new AuditLogSyncBatchRequest(
        [
            new AuditLogSyncRequest(auditLogId, "admin", "ProductUpdated", "Product", Guid.NewGuid(), "Server pushed audit", now)
        ]));

        Assert.Equal("Applied", Assert.Single(response.Results).Status);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var auditLog = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == auditLogId);
        var adminUserId = await db.Users.AsNoTracking().Where(u => u.Username == "admin").Select(u => u.Id).SingleAsync();

        Assert.Equal("ProductUpdated", auditLog.Action);
        Assert.Equal("Product", auditLog.EntityName);
        Assert.Equal("Server pushed audit", auditLog.Details);
        Assert.Equal(adminUserId, auditLog.UserId);
        Assert.Equal(now, auditLog.CreatedAt);
    }

    [Fact]
    public async Task Sync_devices_pull_endpoint_returns_server_device_changes_after_cursor()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var baseline = await GetAndReadAsync<DeviceSyncPullResponse>(
            client,
            "/api/sync/devices/pull?sinceVersion=0:00000000000000000000000000000000&batchSize=200");

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        var deviceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var storeId = await db.Stores.AsNoTracking().Select(s => s.Id).SingleAsync();
            var tenantId = await db.Stores.AsNoTracking().Select(s => s.TenantId).SingleAsync();

            db.Devices.Add(new Device
            {
                Id = deviceId,
                TenantId = tenantId,
                StoreId = storeId,
                Name = "Server Register",
                CreatedAt = now.AddMinutes(-10),
                UpdatedAt = now,
                SyncVersion = 2,
                IsDeleted = false
            });

            await db.SaveChangesAsync();
        }

        var delta = await GetAndReadAsync<DeviceSyncPullResponse>(
            client,
            $"/api/sync/devices/pull?sinceVersion={Uri.EscapeDataString(baseline.NextSinceVersion)}&batchSize=20");

        var pulled = Assert.Single(delta.Devices);
        Assert.Equal(deviceId, pulled.DeviceId);
        Assert.Equal("Server Register", pulled.Name);
        Assert.Equal(2, pulled.SyncVersion);

        var secondPull = await GetAndReadAsync<DeviceSyncPullResponse>(
            client,
            $"/api/sync/devices/pull?sinceVersion={Uri.EscapeDataString(delta.NextSinceVersion)}&batchSize=20");

        Assert.Empty(secondPull.Devices);
        Assert.Equal(delta.NextSinceVersion, secondPull.NextSinceVersion);
    }

    [Fact]
    public async Task Sync_product_push_endpoint_applies_snapshot_on_server()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var now = DateTime.UtcNow;
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        var response = await PostAndReadAsync<ProductSyncPushResponse>(client, "/api/sync/products/push", new ProductSyncBatchRequest(
        [
            new ProductSyncRequest(
                productId,
                "Pushed Product",
                "70001",
                6.5m,
                2m,
                categoryId,
                "Pushed Category",
                11m,
                true,
                null,
                now,
                now,
                false)
        ]));

        Assert.Equal("Applied", Assert.Single(response.Results).Status);

        var product = await GetProductAsync(client, "Pushed Product");
        Assert.Equal(11m, product.QuantityOnHand);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var category = await db.Categories.AsNoTracking().SingleAsync(c => c.Id == categoryId);
        Assert.Equal("Pushed Category", category.Name);
    }

    [Fact]
    public async Task Sync_category_push_endpoint_applies_snapshot_on_server()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var categoryId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var response = await PostAndReadAsync<CategorySyncPushResponse>(client, "/api/sync/categories/push", new CategorySyncBatchRequest(
        [
            new CategorySyncRequest(categoryId, "Pushed Category Only", now.AddMinutes(-1), now, false)
        ]));

        Assert.Equal("Applied", Assert.Single(response.Results).Status);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var category = await db.Categories.AsNoTracking().SingleAsync(c => c.Id == categoryId);
        Assert.Equal("Pushed Category Only", category.Name);
        Assert.Equal(now, category.UpdatedAt);
    }

    [Fact]
    public async Task Sync_devices_push_endpoint_applies_snapshot_on_server()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var deviceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var response = await PostAndReadAsync<DeviceSyncPushResponse>(client, "/api/sync/devices/push", new DeviceSyncBatchRequest(
        [
            new DeviceSyncRequest(deviceId, "Pushed Register", 5, now.AddMinutes(-5), now, false)
        ]));

        Assert.Equal("Applied", Assert.Single(response.Results).Status);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var device = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
        Assert.Equal("Pushed Register", device.Name);
        Assert.Equal(5, device.SyncVersion);
        Assert.Equal(now, device.UpdatedAt);
    }

    [Fact]
    public async Task Sync_devices_push_endpoint_skips_older_same_name_snapshot_without_changing_server_device_identity()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var serverDeviceId = Guid.NewGuid();
        var incomingDeviceId = Guid.NewGuid();
        var serverUpdatedAt = DateTime.UtcNow;
        var incomingUpdatedAt = serverUpdatedAt.AddMinutes(-10);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var storeId = await db.Stores.AsNoTracking().Select(s => s.Id).SingleAsync();
            var tenantId = await db.Stores.AsNoTracking().Select(s => s.TenantId).SingleAsync();

            db.Devices.Add(new Device
            {
                Id = serverDeviceId,
                TenantId = tenantId,
                StoreId = storeId,
                Name = "Shared Register",
                CreatedAt = serverUpdatedAt.AddMinutes(-20),
                UpdatedAt = serverUpdatedAt,
                SyncVersion = 6,
                IsDeleted = false
            });

            await db.SaveChangesAsync();
        }

        var response = await PostAndReadAsync<DeviceSyncPushResponse>(client, "/api/sync/devices/push", new DeviceSyncBatchRequest(
        [
            new DeviceSyncRequest(incomingDeviceId, "Shared Register", 2, incomingUpdatedAt.AddMinutes(-5), incomingUpdatedAt, false)
        ]));

        Assert.Equal("Skipped", Assert.Single(response.Results).Status);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDbFactory = verifyScope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var verifyDb = await verifyDbFactory.CreateDbContextAsync();
        var device = await verifyDb.Devices.AsNoTracking().SingleAsync(d => d.Id == serverDeviceId);
        var incomingExists = await verifyDb.Devices.AsNoTracking().AnyAsync(d => d.Id == incomingDeviceId);

        Assert.Equal("Shared Register", device.Name);
        Assert.Equal(6, device.SyncVersion);
        Assert.Equal(serverUpdatedAt, device.UpdatedAt);
        Assert.False(incomingExists);
    }

    [Fact]
    public async Task Sync_settings_push_endpoint_applies_snapshot_on_server()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var now = DateTime.UtcNow;

        var response = await PostAndReadAsync<SettingsSyncPushResponse>(client, "/api/sync/settings/push", new SettingsSyncBatchRequest(
        [
            new SettingSyncRequest("ReceiptFooterText", "Server pushed footer", now, now, false)
        ]));

        Assert.Equal("Applied", Assert.Single(response.Results).Status);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var storeId = await db.Stores.AsNoTracking().Select(s => s.Id).SingleAsync();
        var setting = await db.Settings.AsNoTracking().SingleAsync(s => s.StoreId == storeId && s.Key == "ReceiptFooterText");
        Assert.Equal("Server pushed footer", setting.Value);
    }

    [Fact]
    public async Task Sync_users_push_endpoint_applies_snapshot_on_server_and_creates_missing_role()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var response = await PostAndReadAsync<UserSyncPushResponse>(client, "/api/sync/users/push", new UserSyncBatchRequest(
        [
            new UserSyncRequest(userId, "pushed.manager", "manager-hash", "Manager", true, now.AddMinutes(-1), now, false)
        ]));

        Assert.Equal("Applied", Assert.Single(response.Results).Status);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var pushedUser = await db.Users.AsNoTracking().SingleAsync(u => u.Username == "pushed.manager");
        var roleName = await db.Roles.AsNoTracking().Where(r => r.Id == pushedUser.RoleId).Select(r => r.Name).SingleAsync();

        Assert.Equal(userId, pushedUser.Id);
        Assert.Equal("manager-hash", pushedUser.PasswordHash);
        Assert.True(pushedUser.IsActive);
        Assert.Equal("Manager", roleName);
    }

    [Fact]
    public async Task Sync_currency_policy_pull_endpoint_returns_newer_server_policy()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        var baseline = await GetAndReadAsync<CurrencyPolicySyncPullResultDto>(client, "/api/sync/currency-policy/pull?sinceVersion=0");
        Assert.NotNull(baseline.Policy);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        Guid newBaseId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var store = await db.Stores.SingleAsync();
            var currencies = await db.Currencies.OrderBy(c => c.Code).ToListAsync();
            var nextBase = currencies.First(c => c.Id != store.BaseCurrencyId);
            newBaseId = nextBase.Id;
            var now = DateTime.UtcNow;

            await db.Stores
                .Where(s => s.Id == store.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.BaseCurrencyId, nextBase.Id)
                    .SetProperty(s => s.UpdatedAt, now));

            var rates = await db.TenantCurrencyRates
                .Where(r => r.TenantId == store.TenantId)
                .ToDictionaryAsync(r => r.CurrencyId);

            foreach (var currency in currencies)
            {
                var currentRate = rates.TryGetValue(currency.Id, out var r) ? r.ExchangeRate : 1m;
                var nextRate = currency.Id == nextBase.Id ? 1m : currentRate + 0.25m;
                await db.TenantCurrencyRates
                    .Where(r => r.TenantId == store.TenantId && r.CurrencyId == currency.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(r => r.ExchangeRate, nextRate)
                        .SetProperty(r => r.UpdatedAt, now));
            }

            db.SyncChanges.Add(new SyncChange
            {
                TenantId = store.TenantId,
                StoreId = store.Id,
                AggregateType = POS.Core.SyncAggregateTypes.CurrencyPolicy,
                EntityId = store.Id,
                ChangedAt = now
            });

            await db.SaveChangesAsync();
        }

        var delta = await GetAndReadAsync<CurrencyPolicySyncPullResultDto>(
            client,
            $"/api/sync/currency-policy/pull?sinceVersion={Uri.EscapeDataString(baseline.NextSinceVersion)}");

        Assert.NotNull(delta.Policy);
        Assert.Equal(newBaseId, delta.Policy.BaseCurrencyId);
        Assert.NotEmpty(delta.Policy.Currencies);
        Assert.True(long.TryParse(delta.NextSinceVersion, out var nextSequence) && nextSequence > 0);
        Assert.NotEqual(baseline.NextSinceVersion, delta.NextSinceVersion);
    }

    [Fact]
    public async Task Sync_currency_policy_push_endpoint_applies_newer_policy_on_server()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var store = await db.Stores.AsNoTracking().SingleAsync();
        var currencies = await db.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
        var rates = await db.TenantCurrencyRates
            .AsNoTracking()
            .Where(r => r.TenantId == store.TenantId)
            .ToDictionaryAsync(r => r.CurrencyId, r => r.ExchangeRate);
        var changedAt = DateTime.UtcNow.AddMinutes(5);
        var updatedCurrencies = currencies
            .Select(c => new CurrencyDto(c.Id, c.Code, c.Name, c.Symbol, c.Id == store.BaseCurrencyId ? 1m : (rates.TryGetValue(c.Id, out var rate) ? rate : 1m) + 0.5m))
            .ToList();

        var response = await PostAndReadAsync<CurrencyPolicySyncPushResultDto>(client, "/api/sync/currency-policy/push", new CurrencyPolicySyncDto(
            store.Id,
            store.BaseCurrencyId,
            changedAt,
            updatedCurrencies));

        Assert.True(string.Equals(response.Status, "Applied", StringComparison.Ordinal), response.ErrorMessage ?? "Expected an applied currency policy snapshot.");
    }

    [Fact]
    public async Task Platform_tenant_provisioning_is_disabled_without_a_configured_secret()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/platform/tenants", new ProvisionTenantRequest(
            "Second Business", "second-business", "Main Store", "owner", "OwnerPassword1!"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Platform_tenant_provisioning_requires_the_configured_secret_and_creates_a_working_tenant()
    {
        const string provisioningSecret = "test-provisioning-secret";

        using var factory = new ApiTestFactory(new Dictionary<string, string?>
        {
            ["Platform:ProvisioningSecret"] = provisioningSecret
        });
        using var client = factory.CreateClient();

        var request = new ProvisionTenantRequest("Second Business", "second-business", "Main Store", "owner", "OwnerPassword1!");

        var noSecretResponse = await client.PostAsJsonAsync("/api/platform/tenants", request);
        Assert.Equal(HttpStatusCode.Unauthorized, noSecretResponse.StatusCode);

        var wrongSecretResponse = await SendWithSecretAsync(client, request, "not-the-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecretResponse.StatusCode);

        var correctResponse = await SendWithSecretAsync(client, request, provisioningSecret);
        var result = await ReadRequiredAsync<ProvisionTenantResponse>(correctResponse);
        Assert.NotEqual(Guid.Empty, result.TenantId);
        Assert.NotEqual(Guid.Empty, result.StoreId);
        Assert.NotEqual(Guid.Empty, result.AdminUserId);

        var duplicateResponse = await SendWithSecretAsync(client, request, provisioningSecret);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode);

        // The new tenant's admin can now sign in with its slug — the tenant-scoped login this unblocks.
        var newTenantLogin = await PostAndReadAsync<LoginResponse>(
            client, "/api/auth/login", new LoginRequest("owner", "OwnerPassword1!", "second-business"));
        Assert.False(string.IsNullOrWhiteSpace(newTenantLogin.AccessToken));

        // The original bootstrap tenant's admin still resolves independently now that two tenants exist.
        var bootstrapLogin = await PostAndReadAsync<LoginResponse>(
            client, "/api/auth/login", new LoginRequest("admin", POS.Infrastructure.Data.DatabaseSeeder.DemoAdminPassword, "default"));
        Assert.False(string.IsNullOrWhiteSpace(bootstrapLogin.AccessToken));
    }

    private static async Task<HttpResponseMessage> SendWithSecretAsync(HttpClient client, object body, string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Provisioning-Secret", secret);
        return await client.SendAsync(request);
    }

    private static async Task AuthorizeAsync(HttpClient client)
    {
        var login = await PostAndReadAsync<LoginResponse>(client, "/api/auth/login", new LoginRequest("admin", POS.Infrastructure.Data.DatabaseSeeder.DemoAdminPassword));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
    }

    private static async Task<ProductResponse> GetProductAsync(HttpClient client, string name)
    {
        var products = await client.GetFromJsonAsync<List<ProductResponse>>($"/api/catalog/products?query={Uri.EscapeDataString(name)}");
        Assert.NotNull(products);
        return Assert.Single(products, p => p.Name == name);
    }

    private static async Task<T> PostAndReadAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsync(url, JsonContent.Create(body, body.GetType()));
        var payload = await ReadRequiredAsync<T>(response);
        return payload;
    }

    private static async Task<T> PatchAndReadAsync<T>(HttpClient client, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body, body.GetType())
        };
        var response = await client.SendAsync(request);
        var payload = await ReadRequiredAsync<T>(response);
        return payload;
    }

    private static async Task<T> GetAndReadAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var payload = await ReadRequiredAsync<T>(response);
        return payload;
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, content);

        var payload = await response.Content.ReadFromJsonAsync<T>();
        return Assert.IsType<T>(payload);
    }

    private sealed record LoginRequest(string Username, string Password, string? TenantSlug = null);
    private sealed record ProvisionTenantRequest(string TenantName, string TenantSlug, string StoreName, string AdminUsername, string AdminPassword);
    private sealed record ProvisionTenantResponse(Guid TenantId, Guid StoreId, Guid AdminUserId);
    private sealed record LoginResponse(string AccessToken, DateTime ExpiresAtUtc, Guid UserId, Guid StoreId, string Username, string RoleName, string BaseCurrencyCode, string? CurrencySymbol);
    private sealed record CurrentUserResponse(Guid UserId, Guid StoreId, string Username, string RoleName, string BaseCurrencyCode, string? CurrencySymbol);
    private sealed record CategoryResponse(Guid Id, string Name);
    private sealed record ProductResponse(Guid Id, string Name, string? Barcode, decimal Price, decimal QuantityOnHand, bool IsLowStock, string? ImagePath);
    private sealed record InvoiceSummaryResponse(decimal Subtotal, decimal TaxPercent, decimal TaxAmount, decimal Total);
    private sealed record CartLineResponse(Guid LineId, Guid ProductId, string ProductName, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal LineTotal);
    private sealed record SaleSnapshotResponse(Guid InvoiceId, InvoiceSummaryResponse Summary, IReadOnlyList<CartLineResponse> Lines);
    private sealed record SaleLifecycleResponse(Guid InvoiceId, string Status, InvoiceSummaryResponse Summary, IReadOnlyList<CartLineResponse> Lines);
    private sealed record AddSaleLineRequest(Guid ProductId, decimal Quantity);
    private sealed record UpdateSaleLineQuantityRequest(decimal Quantity);
    private sealed record UpdateSaleLineDiscountRequest(decimal DiscountPercent);
    private sealed record UpdateSaleTaxRequest(decimal TaxPercent);
    private sealed record CompleteCashSaleRequest(decimal CashTendered);
    private sealed record ReceiptLineResponse(string Name, decimal Quantity, decimal UnitPrice, decimal LineTotal);
    private sealed record ReceiptResponse(string StoreName, string InvoiceNumber, DateTime PaidAt, string Currency, decimal Total, decimal CashTendered, decimal Change, IReadOnlyList<ReceiptLineResponse> Lines);
    private sealed record CompleteCashSaleResponse(ReceiptResponse Receipt);
    private sealed record InvoiceSyncBatchRequest(IReadOnlyList<InvoiceSyncInvoiceRequest> Invoices);
    private sealed record InvoiceSyncInvoiceRequest(Guid InvoiceId, Guid? DeviceId, string? DeviceName, int SyncVersion, int Status, decimal TotalAmount, decimal TaxPercent, string Currency, string? Notes, DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<InvoiceSyncLineRequest> Lines, IReadOnlyList<InvoiceSyncPaymentRequest> Payments, string? Username = null);
    private sealed record InvoiceSyncLineRequest(Guid LineId, Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal LineTotal, DateTime CreatedAt, DateTime UpdatedAt, bool IsDeleted);
    private sealed record InvoiceSyncPaymentRequest(Guid PaymentId, decimal Amount, int Method, DateTime PaidAt, DateTime CreatedAt, DateTime UpdatedAt, bool IsDeleted);
    private sealed record InvoiceSyncPushResultResponse(IReadOnlyList<InvoiceSyncInvoiceResultResponse> Results);
    private sealed record InvoiceSyncInvoiceResultResponse(Guid InvoiceId, string Status, int ServerSyncVersion, string? ErrorMessage);
    private sealed record InvoiceSyncPullResponse(IReadOnlyList<InvoiceSyncInvoiceResponse> Invoices, string NextSinceVersion);
    private sealed record InvoiceSyncInvoiceResponse(Guid InvoiceId, int SyncVersion, int Status, string Currency, string? Username);
    private sealed record CategorySyncPullResponse(IReadOnlyList<CategorySyncResponse> Categories, string NextSinceVersion);
    private sealed record CategorySyncResponse(Guid CategoryId, string Name, DateTime CreatedAt, DateTime UpdatedAt, bool IsDeleted);
    private sealed record DeviceSyncPullResponse(IReadOnlyList<DeviceSyncResponse> Devices, string NextSinceVersion);
    private sealed record DeviceSyncResponse(Guid DeviceId, string Name, int SyncVersion, DateTime CreatedAt, DateTime UpdatedAt, bool IsDeleted);
    private sealed record CategorySyncBatchRequest(IReadOnlyList<CategorySyncRequest> Categories);
    private sealed record CategorySyncRequest(Guid CategoryId, string Name, DateTime CreatedAt, DateTime UpdatedAt, bool IsDeleted);
    private sealed record CategorySyncPushResponse(IReadOnlyList<CategorySyncPushItemResponse> Results);
    private sealed record CategorySyncPushItemResponse(Guid CategoryId, string Status, DateTime ServerUpdatedAt, string? ErrorMessage);
    private sealed record DeviceSyncBatchRequest(IReadOnlyList<DeviceSyncRequest> Devices);
    private sealed record DeviceSyncRequest(Guid DeviceId, string Name, int SyncVersion, DateTime CreatedAt, DateTime UpdatedAt, bool IsDeleted);
    private sealed record DeviceSyncPushResponse(IReadOnlyList<DeviceSyncPushItemResponse> Results);
    private sealed record DeviceSyncPushItemResponse(Guid DeviceId, string Status, DateTime ServerUpdatedAt, string? ErrorMessage);
    private sealed record ProductSyncPullResponse(IReadOnlyList<ProductSyncResponse> Products, string NextSinceVersion);
    private sealed record ProductSyncResponse(Guid ProductId, string Name, decimal QuantityOnHand, Guid CategoryId, string CategoryName, bool IsActive, bool IsDeleted);
    private sealed record ProductSyncBatchRequest(IReadOnlyList<ProductSyncRequest> Products);
    private sealed record ProductSyncRequest(Guid ProductId, string Name, string? Barcode, decimal Price, decimal Cost, Guid CategoryId, string CategoryName, decimal QuantityOnHand, bool IsActive, string? ImagePath, DateTime CreatedAt, DateTime UpdatedAt, bool IsDeleted);
    private sealed record ProductSyncPushResponse(IReadOnlyList<ProductSyncPushItemResponse> Results);
    private sealed record ProductSyncPushItemResponse(Guid ProductId, string Status, DateTime ServerUpdatedAt, string? ErrorMessage);
    private sealed record SettingsSyncPullResponse(IReadOnlyList<SettingSyncResponse> Settings, string NextSinceVersion);
    private sealed record SettingSyncResponse(string Key, string Value, bool IsDeleted);
    private sealed record UserSyncPullResponse(IReadOnlyList<UserSyncResponse> Users, string NextSinceVersion);
    private sealed record UserSyncResponse(Guid UserId, string Username, string PasswordHash, string RoleName, bool IsActive, bool IsDeleted);
    private sealed record AuditLogSyncBatchRequest(IReadOnlyList<AuditLogSyncRequest> AuditLogs);
    private sealed record AuditLogSyncRequest(Guid Id, string? Username, string Action, string EntityName, Guid? EntityId, string? Details, DateTime CreatedAt);
    private sealed record AuditLogSyncPushResponse(IReadOnlyList<AuditLogSyncPushItemResponse> Results);
    private sealed record AuditLogSyncPushItemResponse(Guid AuditLogId, string Status, DateTime ServerCreatedAt, string? ErrorMessage);
    private sealed record AuditLogSyncPullResponse(IReadOnlyList<AuditLogSyncResponse> AuditLogs, string NextSinceVersion);
    private sealed record AuditLogSyncResponse(Guid Id, string? Username, string Action, string EntityName, Guid? EntityId, string? Details, DateTime CreatedAt);
    private sealed record SettingsSyncBatchRequest(IReadOnlyList<SettingSyncRequest> Settings);
    private sealed record SettingSyncRequest(string Key, string Value, DateTime CreatedAt, DateTime UpdatedAt, bool IsDeleted);
    private sealed record SettingsSyncPushResponse(IReadOnlyList<SettingSyncPushItemResponse> Results);
    private sealed record SettingSyncPushItemResponse(string Key, string Status, DateTime ServerUpdatedAt, string? ErrorMessage);
    private sealed record UserSyncBatchRequest(IReadOnlyList<UserSyncRequest> Users);
    private sealed record UserSyncRequest(Guid UserId, string Username, string PasswordHash, string RoleName, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt, bool IsDeleted);
    private sealed record UserSyncPushResponse(IReadOnlyList<UserSyncPushItemResponse> Results);
    private sealed record UserSyncPushItemResponse(Guid UserId, string Status, DateTime ServerUpdatedAt, string? ErrorMessage);
}