using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class InvoiceSyncServiceTests
{
    // Regression coverage for a real bug: once /api/auth/login started verifying a real password
    // (Stage 4T/T2), the sync worker's internal re-login call sent no Password field at all and would
    // have been rejected by any real AuthService — sync silently never succeeded end to end. These two
    // tests fail on the old `new { Username = _session.Username }` login body without needing a real
    // AuthService, by asserting on what TryCreateAuthorizedClientAsync actually sends/does.
    [Fact]
    public async Task Push_unsynced_invoices_sends_the_signed_in_users_cached_password_to_the_api_login_endpoint()
    {
        string? capturedPassword = "not-captured";

        var handler = new FakeSyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                var body = await request.Content!.ReadFromJsonAsync<LoginRequestBody>(cancellationToken: cancellationToken);
                capturedPassword = body?.Password;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/invoices/push")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new InvoiceSyncPushResultDto(Array.Empty<InvoiceSyncInvoiceResultDto>()))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sales = services.GetRequiredService<ISaleService>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Sync Password Item",
                Price = 12m,
                Cost = 5m,
                CategoryId = host.CategoryId,
                InitialStock = 20m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();
            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 1m);

            await sync.PushUnsyncedInvoicesAsync();

            Assert.Equal("admin", capturedPassword);
        });
    }

    [Fact]
    public async Task Push_unsynced_invoices_fails_safe_without_attempting_login_when_session_has_no_cached_password()
    {
        var loginAttempted = false;

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
                loginAttempted = true;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sales = services.GetRequiredService<ISaleService>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var session = services.GetRequiredService<ICurrentSession>();

            session.SetPassword(null);

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Sync No Password Item",
                Price = 12m,
                Cost = 5m,
                CategoryId = host.CategoryId,
                InitialStock = 20m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();
            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 1m);

            var summary = await sync.PushUnsyncedInvoicesAsync();

            Assert.False(loginAttempted);
            Assert.Equal(1, summary.Attempted);
            Assert.Equal(1, summary.Failed);
        });
    }

    private sealed record LoginRequestBody(string? Username, string? Password);

    [Fact]
    public async Task Push_unsynced_invoices_records_last_online_contact_after_a_successful_login()
    {
        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/invoices/push")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new InvoiceSyncPushResultDto(Array.Empty<InvoiceSyncInvoiceResultDto>()))
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        var beforeLogin = DateTime.UtcNow;

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sales = services.GetRequiredService<ISaleService>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            Assert.Null(host.Session.LastOnlineContactUtc);

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Online Contact Item",
                Price = 12m,
                Cost = 5m,
                CategoryId = host.CategoryId,
                InitialStock = 20m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();
            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 1m);

            await sync.PushUnsyncedInvoicesAsync();

            Assert.NotNull(host.Session.LastOnlineContactUtc);
            Assert.InRange(host.Session.LastOnlineContactUtc!.Value, beforeLogin, DateTime.UtcNow);

            await using var db = await dbFactory.CreateDbContextAsync();
            var persisted = await db.Settings
                .AsNoTracking()
                .Where(s => s.StoreId == host.StoreId && s.Key == "Sync.LastOnlineContactUtc" && !s.IsDeleted)
                .Select(s => s.Value)
                .SingleAsync();

            var persistedUtc = new DateTime(long.Parse(persisted), DateTimeKind.Utc);
            Assert.InRange(persistedUtc, beforeLogin, DateTime.UtcNow);
        });
    }

    [Fact]
    public async Task Push_unsynced_invoices_marks_local_invoice_synced_when_server_accepts()
    {
        var results = new InvoiceSyncPushResultDto(
        [
            new InvoiceSyncInvoiceResultDto(Guid.Empty, "Applied", 0, null)
        ]);

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/invoices/push")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sales = services.GetRequiredService<ISaleService>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Sync Push Item",
                Price = 12m,
                Cost = 5m,
                CategoryId = host.CategoryId,
                InitialStock = 20m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();
            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 2m);

            results = new InvoiceSyncPushResultDto(
            [
                new InvoiceSyncInvoiceResultDto(invoiceId, "Applied", 2, null)
            ]);

            var summary = await sync.PushUnsyncedInvoicesAsync();

            Assert.Equal(1, summary.Attempted);
            Assert.Equal(1, summary.Synced);
            Assert.Equal(0, summary.Conflicts);
            Assert.Equal(0, summary.Failed);

            await using var db = await dbFactory.CreateDbContextAsync();
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == invoiceId);
            Assert.True(invoice.IsSynced);
            Assert.Equal(2, invoice.SyncVersion);
        });
    }

    [Fact]
    public async Task Pull_remote_invoices_applies_snapshot_updates_inventory_and_persists_cursor()
    {
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var nextSinceVersion = $"{now.Ticks}:{invoiceId:N}";
        InvoiceSyncPullResultDto results = new(Array.Empty<InvoiceSyncInvoiceDto>(), nextSinceVersion);

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/invoices/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Pulled Item",
                Price = 12m,
                Cost = 5m,
                CategoryId = host.CategoryId,
                InitialStock = 20m,
                IsActive = true
            });

            results = new InvoiceSyncPullResultDto(
            [
                new InvoiceSyncInvoiceDto(
                    invoiceId,
                    deviceId,
                    "Remote Register",
                    3,
                    POS.Core.Enums.InvoiceStatus.Paid,
                    24m,
                    0m,
                    "USD",
                    null,
                    now,
                    now,
                    [new InvoiceSyncLineDto(lineId, product.Id, 2m, 12m, 0m, 24m, now, now, false)],
                    [new InvoiceSyncPaymentDto(paymentId, 24m, POS.Core.Enums.PaymentMethod.Cash, now, now, now, false)],
                    "admin")
            ],
                nextSinceVersion);

            var summary = await sync.PullRemoteInvoicesAsync();

            Assert.Equal(1, summary.Received);
            Assert.Equal(1, summary.Applied);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(nextSinceVersion, summary.NextSinceVersion);

            await using var db = await dbFactory.CreateDbContextAsync();
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == invoiceId);
            var inventory = await db.Inventories.AsNoTracking().SingleAsync(x => x.ProductId == product.Id && x.StoreId == host.StoreId);
            var cursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.InvoicePullSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();

            Assert.True(invoice.IsSynced);
            Assert.Equal(3, invoice.SyncVersion);
            Assert.Equal(POS.Core.Enums.InvoiceStatus.Paid, invoice.Status);
            Assert.Equal(18m, inventory.Quantity);
            Assert.Equal(nextSinceVersion, cursor);
        });
    }

    [Fact]
    public async Task Pull_remote_categories_applies_snapshot_and_persists_cursor()
    {
        var categoryId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var nextSinceVersion = $"{now.Ticks}:{categoryId:N}";
        CategorySyncPullResultDto results = new(Array.Empty<CategorySyncDto>(), nextSinceVersion);

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/categories/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            results = new CategorySyncPullResultDto(
            [
                new CategorySyncDto(categoryId, "Pulled Category", now, now, false)
            ],
                nextSinceVersion);

            var summary = await sync.PullRemoteCategoriesAsync();

            Assert.Equal(1, summary.Received);
            Assert.Equal(1, summary.Applied);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(nextSinceVersion, summary.NextSinceVersion);

            await using var db = await dbFactory.CreateDbContextAsync();
            var category = await db.Categories.AsNoTracking().SingleAsync(x => x.Id == categoryId);
            var cursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.CategoryPullSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();

            Assert.Equal("Pulled Category", category.Name);
            Assert.Equal(nextSinceVersion, cursor);
        });
    }

    [Fact]
    public async Task Pull_remote_products_applies_snapshot_updates_inventory_and_persists_cursor()
    {
        var productId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var nextSinceVersion = $"{now.Ticks}:{productId:N}";
        ProductSyncPullResultDto results = new(Array.Empty<ProductSyncDto>(), nextSinceVersion);

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/products/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            results = new ProductSyncPullResultDto(
            [
                new ProductSyncDto(
                    productId,
                    "Pulled Product",
                    "80001",
                    19m,
                    8m,
                    categoryId,
                    "Pulled Category",
                    14m,
                    true,
                    null,
                    now,
                    now,
                    false)
            ],
                nextSinceVersion);

            var summary = await sync.PullRemoteProductsAsync();

            Assert.Equal(1, summary.Received);
            Assert.Equal(1, summary.Applied);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(nextSinceVersion, summary.NextSinceVersion);

            await using var db = await dbFactory.CreateDbContextAsync();
            var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == productId);
            var category = await db.Categories.AsNoTracking().SingleAsync(x => x.Id == categoryId);
            var inventory = await db.Inventories.AsNoTracking().SingleAsync(x => x.ProductId == productId && x.StoreId == host.StoreId);
            var cursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.ProductPullSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();

            Assert.Equal("Pulled Product", product.Name);
            Assert.Equal("Pulled Category", category.Name);
            Assert.Equal(14m, inventory.Quantity);
            Assert.Equal(nextSinceVersion, cursor);
        });
    }

    [Fact]
    public async Task Pull_remote_settings_applies_snapshot_and_persists_cursor()
    {
        var now = DateTime.UtcNow;
        var nextSinceVersion = $"{now.Ticks}:ReceiptFooterText";
        SettingsSyncPullResultDto results = new(Array.Empty<SettingSyncDto>(), nextSinceVersion);

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/settings/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            results = new SettingsSyncPullResultDto(
            [
                new SettingSyncDto("ReceiptFooterText", "Pulled footer", now, now, false),
                new SettingSyncDto("LowStockThreshold", "9", now, now, false)
            ],
                nextSinceVersion);

            var summary = await sync.PullRemoteSettingsAsync();

            Assert.Equal(2, summary.Received);
            Assert.Equal(2, summary.Applied);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(nextSinceVersion, summary.NextSinceVersion);

            await using var db = await dbFactory.CreateDbContextAsync();
            var footer = await db.Settings.AsNoTracking().SingleAsync(x => x.StoreId == host.StoreId && x.Key == "ReceiptFooterText" && !x.IsDeleted);
            var lowStock = await db.Settings.AsNoTracking().SingleAsync(x => x.StoreId == host.StoreId && x.Key == "LowStockThreshold" && !x.IsDeleted);
            var cursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.SettingsPullSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();

            Assert.Equal("Pulled footer", footer.Value);
            Assert.Equal("9", lowStock.Value);
            Assert.Equal(nextSinceVersion, cursor);
        });
    }

    [Fact]
    public async Task Pull_remote_users_applies_snapshot_creates_role_and_persists_cursor()
    {
        var userId = Guid.NewGuid();
        const string nextSinceVersion = "21";
        var createdAt = DateTime.UtcNow.AddMinutes(-10);
        var updatedAt = DateTime.UtcNow;
        UserSyncPullResultDto results = new(Array.Empty<UserSyncDto>(), nextSinceVersion);

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/users/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            results = new UserSyncPullResultDto(
            [
                new UserSyncDto(userId, "remote.cashier", "hash-123", "Cashier", false, createdAt, updatedAt, false)
            ],
                nextSinceVersion);

            var summary = await sync.PullRemoteUsersAsync();

            Assert.Equal(1, summary.Received);
            Assert.Equal(1, summary.Applied);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(nextSinceVersion, summary.NextSinceVersion);

            await using var db = await dbFactory.CreateDbContextAsync();
            var pulledUser = await db.Users.AsNoTracking().SingleAsync(x => x.Username == "remote.cashier" && x.StoreId == host.StoreId);
            var roleName = await db.Roles.AsNoTracking().Where(x => x.Id == pulledUser.RoleId).Select(x => x.Name).SingleAsync();
            var cursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.UserPullSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();

            Assert.Equal(userId, pulledUser.Id);
            Assert.Equal("hash-123", pulledUser.PasswordHash);
            Assert.False(pulledUser.IsActive);
            Assert.Equal("Cashier", roleName);
            Assert.Equal(nextSinceVersion, cursor);
        });
    }

    [Fact]
    public async Task Pull_remote_users_applies_fresher_role_permissions_and_ignores_stale_ones()
    {
        var userId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddMinutes(-10);
        UserSyncPullResultDto results = new(Array.Empty<UserSyncDto>(), "1");

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/users/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            var firstRoleUpdatedAt = DateTime.UtcNow;
            results = new UserSyncPullResultDto(
            [
                new UserSyncDto(userId, "remote.supervisor", "hash-1", "Supervisor", true, createdAt, DateTime.UtcNow, false,
                    (int)Permission.ViewReports, firstRoleUpdatedAt)
            ], "1");
            await sync.PullRemoteUsersAsync();

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var role = await db.Roles.AsNoTracking().SingleAsync(r => r.Name == "Supervisor");
                Assert.Equal((int)Permission.ViewReports, role.PermissionsMask);
            }

            // Stale update (older RoleUpdatedAt) must be ignored.
            results = new UserSyncPullResultDto(
            [
                new UserSyncDto(userId, "remote.supervisor", "hash-1", "Supervisor", true, createdAt, DateTime.UtcNow.AddSeconds(1), false,
                    (int)Permission.None, firstRoleUpdatedAt.AddMinutes(-5))
            ], "2");
            await sync.PullRemoteUsersAsync();

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var role = await db.Roles.AsNoTracking().SingleAsync(r => r.Name == "Supervisor");
                Assert.Equal((int)Permission.ViewReports, role.PermissionsMask);
            }

            // Fresher update must be applied.
            var freshMask = Permission.ViewReports | Permission.ProcessRefunds;
            results = new UserSyncPullResultDto(
            [
                new UserSyncDto(userId, "remote.supervisor", "hash-1", "Supervisor", true, createdAt, DateTime.UtcNow.AddSeconds(2), false,
                    (int)freshMask, firstRoleUpdatedAt.AddMinutes(5))
            ], "3");
            await sync.PullRemoteUsersAsync();

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var role = await db.Roles.AsNoTracking().SingleAsync(r => r.Name == "Supervisor");
                Assert.Equal((int)freshMask, role.PermissionsMask);
            }
        });
    }

    [Fact]
    public async Task Pull_remote_audit_logs_applies_snapshot_and_persists_cursor()
    {
        var auditLogId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var nextSinceVersion = $"{now.Ticks}:{auditLogId:N}";
        AuditLogSyncPullResultDto results = new(Array.Empty<AuditLogSyncDto>(), nextSinceVersion);

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/audit-logs/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            results = new AuditLogSyncPullResultDto(
            [
                new AuditLogSyncDto(
                    auditLogId,
                    "admin",
                    "ProductUpdated",
                    "Product",
                    Guid.NewGuid(),
                    "Pulled audit row",
                    now)
            ],
                nextSinceVersion);

            var summary = await sync.PullRemoteAuditLogsAsync();

            Assert.Equal(1, summary.Received);
            Assert.Equal(1, summary.Applied);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(nextSinceVersion, summary.NextSinceVersion);

            await using var db = await dbFactory.CreateDbContextAsync();
            var auditLog = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Id == auditLogId && x.StoreId == host.StoreId);
            var cursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.AuditLogsPullSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();

            Assert.Equal("ProductUpdated", auditLog.Action);
            Assert.Equal("Product", auditLog.EntityName);
            Assert.Equal("Pulled audit row", auditLog.Details);
            Assert.Equal(nextSinceVersion, cursor);
        });
    }

    [Fact]
    public async Task Push_updated_audit_logs_sends_snapshots_and_persists_cursor()
    {
        AuditLogSyncPushResultDto results = new(Array.Empty<AuditLogSyncItemResultDto>());
        var captured = new List<AuditLogSyncDto>();

        var handler = new FakeSyncHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/audit-logs/push")
            {
                var body = await request.Content!.ReadFromJsonAsync<AuditLogSyncBatchDto>();
                captured = body!.AuditLogs.ToList();
                results = new AuditLogSyncPushResultDto(captured
                    .Select(a => new AuditLogSyncItemResultDto(a.Id, "Applied", a.CreatedAt, null))
                    .ToList());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var auditLogs = services.GetRequiredService<IAuditLogService>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            await auditLogs.WriteAsync("ProductUpdated", "Product", Guid.NewGuid(), "Pushed audit row");

            var summary = await sync.PushUpdatedAuditLogsAsync();

            Assert.Equal(1, summary.Attempted);
            Assert.Equal(1, summary.Sent);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);

            var pushed = Assert.Single(captured);
            Assert.Equal("ProductUpdated", pushed.Action);
            Assert.Equal("Product", pushed.EntityName);
            Assert.Equal("Pushed audit row", pushed.Details);
            Assert.Equal("admin", pushed.Username);

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var cursor = await verifyDb.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.AuditLogsPushSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();
            var expectedSequence = await verifyDb.SyncChanges.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.AggregateType == POS.Core.SyncAggregateTypes.AuditLog && x.EntityId == pushed.Id)
                .Select(x => (long?)x.Id)
                .MaxAsync();

            Assert.Equal(expectedSequence?.ToString(), cursor);
        });
    }

    [Fact]
    public async Task Pull_remote_devices_applies_snapshot_and_persists_cursor()
    {
        var deviceId = Guid.NewGuid();
        var updatedAt = DateTime.UtcNow;
        var createdAt = updatedAt.AddMinutes(-5);
        var nextSinceVersion = $"{updatedAt.Ticks}:{deviceId:N}";
        DeviceSyncPullResultDto results = new(Array.Empty<DeviceSyncDto>(), nextSinceVersion);

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/devices/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            results = new DeviceSyncPullResultDto(
            [
                new DeviceSyncDto(deviceId, "Remote Register", 4, createdAt, updatedAt, false)
            ],
                nextSinceVersion);

            var summary = await sync.PullRemoteDevicesAsync();

            Assert.Equal(1, summary.Received);
            Assert.Equal(1, summary.Applied);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(nextSinceVersion, summary.NextSinceVersion);

            await using var db = await dbFactory.CreateDbContextAsync();
            var device = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == deviceId && x.StoreId == host.StoreId);
            var cursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.DevicePullSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();

            Assert.Equal("Remote Register", device.Name);
            Assert.Equal(4, device.SyncVersion);
            Assert.Equal(updatedAt, device.UpdatedAt);
            Assert.Equal(nextSinceVersion, cursor);
        });
    }

    [Fact]
    public async Task Pull_remote_devices_skips_older_same_name_snapshot_without_changing_local_device_identity()
    {
        var localDeviceId = Guid.NewGuid();
        var remoteDeviceId = Guid.NewGuid();
        var localUpdatedAt = DateTime.UtcNow;
        var remoteUpdatedAt = localUpdatedAt.AddMinutes(-10);
        var nextSinceVersion = $"{remoteUpdatedAt.Ticks}:{remoteDeviceId:N}";
        DeviceSyncPullResultDto results = new(Array.Empty<DeviceSyncDto>(), nextSinceVersion);

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/devices/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.Devices.Add(new POS.Core.Entities.Device
                {
                    Id = localDeviceId,
                    TenantId = host.TenantId,
                    StoreId = host.StoreId,
                    Name = "Shared Register",
                    CreatedAt = localUpdatedAt.AddMinutes(-20),
                    UpdatedAt = localUpdatedAt,
                    SyncVersion = 7,
                    IsDeleted = false
                });
                await db.SaveChangesAsync();
            }

            results = new DeviceSyncPullResultDto(
            [
                new DeviceSyncDto(remoteDeviceId, "Shared Register", 2, remoteUpdatedAt.AddMinutes(-5), remoteUpdatedAt, false)
            ],
                nextSinceVersion);

            var summary = await sync.PullRemoteDevicesAsync();

            Assert.Equal(1, summary.Received);
            Assert.Equal(0, summary.Applied);
            Assert.Equal(1, summary.Skipped);
            Assert.Equal(0, summary.Failed);

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var device = await verifyDb.Devices.AsNoTracking().SingleAsync(x => x.Id == localDeviceId && x.StoreId == host.StoreId);
            var remoteExists = await verifyDb.Devices.AsNoTracking().AnyAsync(x => x.Id == remoteDeviceId && x.StoreId == host.StoreId);

            Assert.Equal("Shared Register", device.Name);
            Assert.Equal(7, device.SyncVersion);
            Assert.Equal(localUpdatedAt, device.UpdatedAt);
            Assert.False(remoteExists);
        });
    }

    [Fact]
    public async Task Push_updated_devices_sends_snapshots_and_persists_cursor()
    {
        DeviceSyncPushResultDto results = new(Array.Empty<DeviceSyncItemResultDto>());
        var captured = new List<DeviceSyncDto>();

        var handler = new FakeSyncHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/devices/push")
            {
                var body = await request.Content!.ReadFromJsonAsync<DeviceSyncBatchDto>();
                captured = body!.Devices.ToList();
                results = new DeviceSyncPushResultDto(captured
                    .Select(d => new DeviceSyncItemResultDto(d.DeviceId, "Applied", d.UpdatedAt, null))
                    .ToList());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            var deviceId = Guid.NewGuid();
            var updatedAt = DateTime.UtcNow;

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.Devices.Add(new POS.Core.Entities.Device
                {
                    Id = deviceId,
                    TenantId = host.TenantId,
                    StoreId = host.StoreId,
                    Name = "Back Office POS",
                    CreatedAt = updatedAt.AddMinutes(-15),
                    UpdatedAt = updatedAt,
                    SyncVersion = 3,
                    IsDeleted = false
                });
                await db.SaveChangesAsync();
            }

            var summary = await sync.PushUpdatedDevicesAsync();

            Assert.Equal(1, summary.Attempted);
            Assert.Equal(1, summary.Sent);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);

            var pushed = Assert.Single(captured);
            Assert.Equal(deviceId, pushed.DeviceId);
            Assert.Equal("Back Office POS", pushed.Name);
            Assert.Equal(3, pushed.SyncVersion);

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var cursor = await verifyDb.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.DevicePushSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();
            var expectedSequence = await verifyDb.SyncChanges.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.AggregateType == POS.Core.SyncAggregateTypes.Device && x.EntityId == deviceId)
                .Select(x => (long?)x.Id)
                .MaxAsync();

            Assert.Equal(expectedSequence?.ToString(), cursor);
        });
    }

    [Fact]
    public async Task Push_updated_categories_sends_snapshots_and_persists_cursor()
    {
        CategorySyncPushResultDto results = new(Array.Empty<CategorySyncItemResultDto>());
        var captured = new List<CategorySyncDto>();

        var handler = new FakeSyncHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/categories/push")
            {
                var body = await request.Content!.ReadFromJsonAsync<CategorySyncBatchDto>();
                captured = body!.Categories.ToList();
                results = new CategorySyncPushResultDto(captured
                    .Select(c => new CategorySyncItemResultDto(c.CategoryId, "Applied", c.UpdatedAt, null))
                    .ToList());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            await sync.PushUpdatedCategoriesAsync();
            captured.Clear();

            var category = await catalog.CreateCategoryAsync("Push Category");

            var summary = await sync.PushUpdatedCategoriesAsync();

            Assert.Equal(1, summary.Attempted);
            Assert.Equal(1, summary.Sent);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);

            var pushed = Assert.Single(captured);
            Assert.Equal(category.Id, pushed.CategoryId);
            Assert.Equal("Push Category", pushed.Name);

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var cursor = await verifyDb.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.CategoryPushSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();
            var expectedSequence = await verifyDb.SyncChanges.AsNoTracking()
                .Where(x => x.StoreId == null && x.AggregateType == POS.Core.SyncAggregateTypes.Category && x.EntityId == category.Id)
                .Select(x => (long?)x.Id)
                .MaxAsync();

            Assert.Equal(expectedSequence?.ToString(), cursor);
        });
    }

    [Fact]
    public async Task Push_updated_categories_resumes_from_persisted_numeric_sequence_cursor()
    {
        CategorySyncPushResultDto results = new(Array.Empty<CategorySyncItemResultDto>());
        var captured = new List<CategorySyncDto>();

        var handler = new FakeSyncHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/categories/push")
            {
                var body = await request.Content!.ReadFromJsonAsync<CategorySyncBatchDto>();
                captured = body!.Categories.ToList();
                results = new CategorySyncPushResultDto(captured
                    .Select(c => new CategorySyncItemResultDto(c.CategoryId, "Applied", c.UpdatedAt, null))
                    .ToList());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            await sync.PushUpdatedCategoriesAsync();
            captured.Clear();

            var firstCategory = await catalog.CreateCategoryAsync("First Push Category");

            var firstSummary = await sync.PushUpdatedCategoriesAsync();
            Assert.Equal(1, firstSummary.Attempted);
            Assert.Equal(1, firstSummary.Sent);

            captured.Clear();

            var secondCategory = await catalog.CreateCategoryAsync("Second Push Category");

            var secondSummary = await sync.PushUpdatedCategoriesAsync();

            Assert.Equal(1, secondSummary.Attempted);
            Assert.Equal(1, secondSummary.Sent);

            var pushed = Assert.Single(captured);
            Assert.Equal(secondCategory.Id, pushed.CategoryId);
            Assert.DoesNotContain(captured, item => item.CategoryId == firstCategory.Id);

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var expectedSequence = await verifyDb.SyncChanges.AsNoTracking()
                .Where(x => x.StoreId == null && x.AggregateType == POS.Core.SyncAggregateTypes.Category && x.EntityId == secondCategory.Id)
                .Select(x => (long?)x.Id)
                .MaxAsync();

            Assert.Equal(expectedSequence?.ToString(), secondSummary.NextSinceVersion);
        });
    }

    [Fact]
    public async Task Push_updated_products_sends_snapshots_and_persists_cursor()
    {
        ProductSyncPushResultDto results = new(Array.Empty<ProductSyncItemResultDto>());
        var captured = new List<ProductSyncDto>();

        var handler = new FakeSyncHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/products/push")
            {
                var body = await request.Content!.ReadFromJsonAsync<ProductSyncBatchDto>();
                captured = body!.Products.ToList();
                results = new ProductSyncPushResultDto(captured
                    .Select(p => new ProductSyncItemResultDto(p.ProductId, "Applied", p.UpdatedAt, null))
                    .ToList());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Push Product",
                Price = 5m,
                Cost = 2m,
                CategoryId = host.CategoryId,
                InitialStock = 9m,
                IsActive = true
            });

            var summary = await sync.PushUpdatedProductsAsync();

            Assert.Equal(1, summary.Attempted);
            Assert.Equal(1, summary.Sent);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);

            var pushed = Assert.Single(captured);
            Assert.Equal(product.Id, pushed.ProductId);
            Assert.Equal(9m, pushed.QuantityOnHand);

            await using var db = await dbFactory.CreateDbContextAsync();
            var cursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.ProductPushSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();
            var expectedSequence = await db.SyncChanges.AsNoTracking()
                .Where(x => x.StoreId == null && x.AggregateType == POS.Core.SyncAggregateTypes.Product && x.EntityId == product.Id)
                .Select(x => (long?)x.Id)
                .MaxAsync();

            Assert.Equal(expectedSequence?.ToString(), cursor);
        });
    }

    [Fact]
    public async Task Push_updated_settings_sends_snapshots_and_persists_cursor()
    {
        SettingsSyncPushResultDto results = new(Array.Empty<SettingSyncItemResultDto>());
        var captured = new List<SettingSyncDto>();

        var handler = new FakeSyncHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/settings/push")
            {
                var body = await request.Content!.ReadFromJsonAsync<SettingsSyncBatchDto>();
                captured = body!.Settings.ToList();
                results = new SettingsSyncPushResultDto(captured
                    .Select(s => new SettingSyncItemResultDto(s.Key, "Applied", s.UpdatedAt, null))
                    .ToList());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();

            DateTime updatedAt;
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                updatedAt = DateTime.UtcNow;
                db.Settings.Add(new POS.Core.Entities.Setting
                {
                    Id = Guid.NewGuid(),
                    TenantId = host.TenantId,
                    StoreId = host.StoreId,
                    Key = "ReceiptFooterText",
                    Value = "Push footer",
                    CreatedAt = updatedAt,
                    UpdatedAt = updatedAt,
                    IsDeleted = false
                });
                await db.SaveChangesAsync();
            }

            var summary = await sync.PushUpdatedSettingsAsync();

            Assert.Equal(1, summary.Attempted);
            Assert.Equal(1, summary.Sent);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);

            var pushed = Assert.Single(captured);
            Assert.Equal("ReceiptFooterText", pushed.Key);
            Assert.Equal("Push footer", pushed.Value);

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var cursor = await verifyDb.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.SettingsPushSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();
            var expectedSequence = await verifyDb.SyncChanges.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.AggregateType == POS.Core.SyncAggregateTypes.Setting && x.EntityKey == "ReceiptFooterText")
                .Select(x => (long?)x.Id)
                .MaxAsync();

            Assert.Equal(expectedSequence?.ToString(), cursor);
        });
    }
    [Fact]
    public async Task Push_updated_users_sends_snapshots_and_persists_cursor()
    {
        UserSyncPushResultDto results = new(Array.Empty<UserSyncItemResultDto>());
        var captured = new List<UserSyncDto>();

        var handler = new FakeSyncHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/users/push")
            {
                var body = await request.Content!.ReadFromJsonAsync<UserSyncBatchDto>();
                captured = body!.Users.ToList();
                results = new UserSyncPushResultDto(captured
                    .Select(u => new UserSyncItemResultDto(u.UserId, "Applied", u.UpdatedAt, null))
                    .ToList());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(results)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            Guid userId;

            var baseline = await sync.PushUpdatedUsersAsync();
            Assert.True(baseline.Attempted >= 1);
            captured.Clear();

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var roleId = await db.Roles.AsNoTracking().Where(x => x.Name == "Admin").Select(x => x.Id).SingleAsync();
                userId = Guid.NewGuid();
                var now = DateTime.UtcNow;
                db.Users.Add(new POS.Core.Entities.User
                {
                    Id = userId,
                    TenantId = host.TenantId,
                    Username = "push.user",
                    NormalizedUsername = "PUSH.USER",
                    PasswordHash = "push-hash",
                    RoleId = roleId,
                    StoreId = host.StoreId,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                });
                await db.SaveChangesAsync();
            }

            var summary = await sync.PushUpdatedUsersAsync();

            Assert.True(summary.Attempted >= 1);
            Assert.True(summary.Sent >= 1);
            Assert.Equal(0, summary.Failed);

            var pushed = Assert.Single(captured.Where(x => x.UserId == userId));
            Assert.Equal(userId, pushed.UserId);
            Assert.Equal("push.user", pushed.Username);
            Assert.Equal("Admin", pushed.RoleName);
            Assert.Equal("push-hash", pushed.PasswordHash);
            Assert.Equal((int)Permission.All, pushed.RolePermissionsMask);

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var adminRoleUpdatedAt = await verifyDb.Roles.AsNoTracking().Where(r => r.Name == "Admin").Select(r => r.UpdatedAt).SingleAsync();
            Assert.Equal(adminRoleUpdatedAt, pushed.RoleUpdatedAt);
            var cursor = await verifyDb.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.UserPushSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();
            var expectedSequence = await verifyDb.SyncChanges.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.AggregateType == POS.Core.SyncAggregateTypes.User && x.EntityId == userId)
                .Select(x => (long?)x.Id)
                .MaxAsync();

            Assert.True(long.TryParse(cursor, out var persistedSequence));
            Assert.True(expectedSequence.HasValue);
            Assert.True(persistedSequence >= expectedSequence.Value);
        });
    }

    [Fact]
    public async Task Push_currency_policy_sends_snapshot_and_persists_cursor()
    {
        CurrencyPolicySyncDto? captured = null;
        CurrencyPolicySyncPushResultDto result = new("Failed", DateTime.MinValue, null);

        var handler = new FakeSyncHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/currency-policy/push")
            {
                captured = await request.Content!.ReadFromJsonAsync<CurrencyPolicySyncDto>();
                result = new CurrencyPolicySyncPushResultDto("Applied", captured!.UpdatedAt, null);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(result)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var currencies = services.GetRequiredService<ICurrencyService>();
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            await currencies.UpdateStoreCurrencyPolicyAsync(host.BaseCurrencyId,
            [
                new CurrencyRateUpdateDto(host.BaseCurrencyId, 1m),
                new CurrencyRateUpdateDto(host.AltCurrencyId, 1.35m)
            ]);

            var summary = await sync.PushCurrencyPolicyAsync();

            Assert.Equal(1, summary.Attempted);
            Assert.Equal(1, summary.Sent);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);

            var policy = Assert.IsType<CurrencyPolicySyncDto>(captured);
            Assert.Equal(host.StoreId, policy.StoreId);
            Assert.Equal(host.BaseCurrencyId, policy.BaseCurrencyId);
            Assert.Equal(1.35m, policy.Currencies.Single(c => c.Id == host.AltCurrencyId).ExchangeRate);

            await using var db = await dbFactory.CreateDbContextAsync();
            var cursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.CurrencyPolicyPushSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();
            var expectedSequence = await db.SyncChanges.AsNoTracking()
                .Where(x => x.AggregateType == POS.Core.SyncAggregateTypes.CurrencyPolicy
                         && (x.StoreId == host.StoreId || x.StoreId == null))
                .Select(x => (long?)x.Id)
                .MaxAsync();

            Assert.Equal(expectedSequence?.ToString(), cursor);
        });
    }

    [Fact]
    public async Task Pull_currency_policy_applies_snapshot_and_persists_cursors()
    {
        var changedAt = DateTime.UtcNow.AddMinutes(5);
        const string nextSinceVersion = "42";
        CurrencyPolicySyncPullResultDto result = new(null, nextSinceVersion);

        var handler = new FakeSyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "test-token" })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/api/sync/currency-policy/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(result)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();
            var currencies = services.GetRequiredService<ICurrencyService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            result = new CurrencyPolicySyncPullResultDto(
                new CurrencyPolicySyncDto(
                    host.StoreId,
                    host.AltCurrencyId,
                    changedAt,
                    [
                        new CurrencyDto(host.BaseCurrencyId, "USD", "US Dollar", "$", 1.1m),
                        new CurrencyDto(host.AltCurrencyId, "EUR", "Euro", "EUR", 1m)
                    ]),
                nextSinceVersion);

            var summary = await sync.PullCurrencyPolicyAsync();

            Assert.Equal(1, summary.Received);
            Assert.Equal(1, summary.Applied);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);

            var policy = await currencies.GetStoreCurrencyPolicyAsync();
            Assert.Equal(host.AltCurrencyId, policy.BaseCurrencyId);
            Assert.Equal(1m, policy.Currencies.Single(c => c.Id == host.AltCurrencyId).ExchangeRate);

            await using var db = await dbFactory.CreateDbContextAsync();
            var pullCursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.CurrencyPolicyPullSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();
            var pushCursor = await db.Settings.AsNoTracking()
                .Where(x => x.StoreId == host.StoreId && x.Key == "Sync.CurrencyPolicyPushSinceVersion" && !x.IsDeleted)
                .Select(x => x.Value)
                .SingleAsync();
            var expectedPushSequence = await db.SyncChanges.AsNoTracking()
                .Where(x => x.AggregateType == POS.Core.SyncAggregateTypes.CurrencyPolicy
                         && (x.StoreId == host.StoreId || x.StoreId == null))
                .Select(x => (long?)x.Id)
                .MaxAsync();

            Assert.Equal(nextSinceVersion, pullCursor);
            Assert.Equal(expectedPushSequence?.ToString(), pushCursor);
        });
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://sync.example.test/")
        };
    }

    private sealed class FakeSyncHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}