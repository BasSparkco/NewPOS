using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class WebIntegrationTests
{
    [Fact]
    public async Task Login_flow_redirects_unauthenticated_dashboard_requests_and_then_renders_dashboard()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var unauthenticated = await client.GetAsync("/Dashboard");
        Assert.Equal(HttpStatusCode.Redirect, unauthenticated.StatusCode);
        Assert.NotNull(unauthenticated.Headers.Location);
        Assert.Equal("/account/login", unauthenticated.Headers.Location!.AbsolutePath);
        Assert.Contains("ReturnUrl=", unauthenticated.Headers.Location.Query, StringComparison.OrdinalIgnoreCase);

        var loginResult = await LoginAsAdminAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, loginResult.StatusCode);
        Assert.Equal("/", loginResult.Headers.Location?.OriginalString);

        var dashboard = await client.GetAsync("/Dashboard");
        var html = await ReadHtmlAsync(dashboard);

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("Top products", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Low-stock watchlist", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sign_in_is_rejected_for_a_user_with_no_granted_permission()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // Seeded Cashier role carries Permission.None — no dashboard-worthy permission at all.
        const string password = "NoPermissionCashier1!";
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
                Username = "no.permission.cashier",
                NormalizedUsername = "NO.PERMISSION.CASHIER",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                RoleId = cashierRoleId,
                StoreId = storeId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsDeleted = false
            });
            await db.SaveChangesAsync();
        }

        var loginResult = await LoginAsync(client, "no.permission.cashier", password);
        var html = await ReadHtmlAsync(loginResult);

        Assert.Equal(HttpStatusCode.OK, loginResult.StatusCode);
        Assert.Contains("Dashboard access requires at least one granted permission", html, StringComparison.OrdinalIgnoreCase);

        var dashboardAttempt = await client.GetAsync("/Dashboard");
        Assert.Equal(HttpStatusCode.Redirect, dashboardAttempt.StatusCode);
        Assert.Equal("/account/login", dashboardAttempt.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task User_without_ManageUsers_permission_cannot_create_users()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // A custom role with ManageProducts but not ManageUsers — should reach the dashboard
        // (DashboardAccess only needs any permission) but be refused the user-creation action.
        const string password = "ProductsOnlyManager1!";
        Guid cashierRoleId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var storeId = await db.Stores.AsNoTracking().Select(s => s.Id).SingleAsync();
            var tenantId = await db.Stores.AsNoTracking().Select(s => s.TenantId).SingleAsync();
            cashierRoleId = await db.Roles.AsNoTracking().Where(r => r.Name == "Cashier").Select(r => r.Id).SingleAsync();

            var now = DateTime.UtcNow;
            var roleId = Guid.NewGuid();
            db.Roles.Add(new Role
            {
                Id = roleId,
                TenantId = tenantId,
                Name = "Products Only",
                PermissionsMask = (int)Permission.ManageProducts,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Username = "products.only",
                NormalizedUsername = "PRODUCTS.ONLY",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                RoleId = roleId,
                StoreId = storeId,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });
            await db.SaveChangesAsync();
        }

        await LoginAsync(client, "products.only", password);

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);
        Assert.Equal(HttpStatusCode.OK, managementPage.StatusCode);
        Assert.DoesNotContain("id=\"new-username\"", managementHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Read-only", managementHtml, StringComparison.OrdinalIgnoreCase);

        var createUserResponse = await client.PostAsync(
            "/Management/CreateUser",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Username"] = "should.not.be.created",
                ["RoleId"] = cashierRoleId.ToString(),
                ["IsActive"] = "true"
            }));

        // Cookie auth's Forbid() redirects to AccessDeniedPath rather than returning a raw 403.
        Assert.Equal(HttpStatusCode.Redirect, createUserResponse.StatusCode);
        Assert.Equal("/account/access-denied", createUserResponse.Headers.Location?.AbsolutePath);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDbFactory = verifyScope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var verifyDb = await verifyDbFactory.CreateDbContextAsync();
        Assert.False(await verifyDb.Users.AnyAsync(u => u.Username == "should.not.be.created"));
    }

    [Fact]
    public async Task Management_postbacks_create_user_and_persist_operational_settings()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await LoginAsAdminAsync(client);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        Guid cashierRoleId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            cashierRoleId = await db.Roles
                .AsNoTracking()
                .Where(role => role.Name == "Cashier")
                .Select(role => role.Id)
                .SingleAsync();
        }

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);
        Assert.Contains("Store administration", managementHtml, StringComparison.OrdinalIgnoreCase);

        var createUserResponse = await client.PostAsync(
            "/Management/CreateUser",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Username"] = "web.cashier",
                ["RoleId"] = cashierRoleId.ToString(),
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createUserResponse.StatusCode);
        Assert.Equal("/Management", createUserResponse.Headers.Location?.OriginalString);

        var refreshedManagement = await client.GetAsync("/Management");
        var refreshedHtml = await ReadHtmlAsync(refreshedManagement);

        var updateSettingsResponse = await client.PostAsync(
            "/Management/UpdateOperationalSettings",
            BuildFormContent(refreshedHtml, new Dictionary<string, string>
            {
                ["AllowNegativeStock"] = "true",
                ["LowStockThreshold"] = "3.5",
                ["DefaultTaxPercent"] = "8.25",
                ["ReceiptFooterText"] = "Managed by web integration test"
            }));

        Assert.Equal(HttpStatusCode.Redirect, updateSettingsResponse.StatusCode);
        Assert.Equal("/Management", updateSettingsResponse.Headers.Location?.OriginalString);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDbFactory = verificationScope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var verificationDb = await verificationDbFactory.CreateDbContextAsync();

        var createdUser = await verificationDb.Users
            .AsNoTracking()
            .SingleAsync(user => user.Username == "web.cashier" && !user.IsDeleted);

        Assert.Equal(cashierRoleId, createdUser.RoleId);
        Assert.True(createdUser.IsActive);

        var storeId = await verificationDb.Stores
            .AsNoTracking()
            .Select(store => store.Id)
            .SingleAsync();

        var settings = await verificationDb.Settings
            .AsNoTracking()
            .Where(setting => setting.StoreId == storeId && !setting.IsDeleted)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value);

        Assert.Equal("true", settings["AllowNegativeStock"]);
        Assert.Equal("3.5", settings["LowStockThreshold"]);
        Assert.Equal("8.25", settings["DefaultTaxPercent"]);
        Assert.Equal("Managed by web integration test", settings["ReceiptFooterText"]);
    }

    [Fact]
    public async Task Management_postbacks_create_rename_and_delete_category_around_product_usage()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await LoginAsAdminAsync(client);

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);

        var createCategoryResponse = await client.PostAsync(
            "/Management/CreateCategory",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Name"] = "Web Specials"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createCategoryResponse.StatusCode);
        Assert.Equal("/Management", createCategoryResponse.Headers.Location?.OriginalString);

        var refreshedPage = await client.GetAsync("/Management");
        var refreshedHtml = await ReadHtmlAsync(refreshedPage);
        Assert.Contains("Web Specials", refreshedHtml, StringComparison.OrdinalIgnoreCase);

        Guid categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            categoryId = await db.Categories
                .AsNoTracking()
                .Where(category => category.Name == "Web Specials" && !category.IsDeleted)
                .Select(category => category.Id)
                .SingleAsync();
        }

        var renameCategoryResponse = await client.PostAsync(
            "/Management/UpdateCategory",
            BuildFormContent(refreshedHtml, new Dictionary<string, string>
            {
                ["CategoryId"] = categoryId.ToString(),
                ["Name"] = "Web Specials Updated"
            }));

        Assert.Equal(HttpStatusCode.Redirect, renameCategoryResponse.StatusCode);
        Assert.Equal("/Management", renameCategoryResponse.Headers.Location?.OriginalString);

        var renamedPage = await client.GetAsync("/Management");
        var renamedHtml = await ReadHtmlAsync(renamedPage);
        Assert.Contains("Web Specials Updated", renamedHtml, StringComparison.OrdinalIgnoreCase);

        var createProductResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(renamedHtml, new Dictionary<string, string>
            {
                ["Name"] = "Web Category Product",
                ["Barcode"] = "99110",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "7.25",
                ["Cost"] = "2.10",
                ["InitialStock"] = "3",
                ["ImagePath"] = "images/web-category-product.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createProductResponse.StatusCode);
        Assert.Equal("/Management", createProductResponse.Headers.Location?.OriginalString);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDbFactory = verificationScope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var verificationDb = await verificationDbFactory.CreateDbContextAsync();

        var product = await verificationDb.Products
            .AsNoTracking()
            .SingleAsync(row => row.Name == "Web Category Product" && !row.IsDeleted);

        Assert.Equal(categoryId, product.CategoryId);

        var deleteProductPage = await client.GetAsync("/Management");
        var deleteProductHtml = await ReadHtmlAsync(deleteProductPage);

        Assert.Contains("Delete unavailable until linked products are removed.", deleteProductHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(
            $"<form[^>]*class=\"inline-category-form\"[\\s\\S]*?<input type=\"hidden\" name=\"CategoryId\" value=\"{Regex.Escape(categoryId.ToString())}\"[\\s\\S]*?formaction=\"/Management/DeleteCategory\"",
            deleteProductHtml);

        var deleteProductResponse = await client.PostAsync(
            "/Management/DeleteProduct",
            BuildFormContent(deleteProductHtml, new Dictionary<string, string>
            {
                ["productId"] = product.Id.ToString()
            }));

        Assert.Equal(HttpStatusCode.Redirect, deleteProductResponse.StatusCode);
        Assert.Equal("/Management", deleteProductResponse.Headers.Location?.OriginalString);

        var deleteCategoryPage = await client.GetAsync("/Management");
        var deleteCategoryHtml = await ReadHtmlAsync(deleteCategoryPage);
        Assert.Matches(
            $"<form[^>]*class=\"inline-category-form\"[\\s\\S]*?<input type=\"hidden\" name=\"CategoryId\" value=\"{Regex.Escape(categoryId.ToString())}\"[\\s\\S]*?formaction=\"/Management/DeleteCategory\"",
            deleteCategoryHtml);

        var deleteCategoryResponse = await client.PostAsync(
            "/Management/DeleteCategory",
            BuildFormContent(deleteCategoryHtml, new Dictionary<string, string>
            {
                ["categoryId"] = categoryId.ToString()
            }));

        Assert.Equal(HttpStatusCode.Redirect, deleteCategoryResponse.StatusCode);
        Assert.Equal("/Management", deleteCategoryResponse.Headers.Location?.OriginalString);

        await using var deletedVerificationScope = factory.Services.CreateAsyncScope();
        var deletedVerificationDbFactory = deletedVerificationScope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var deletedVerificationDb = await deletedVerificationDbFactory.CreateDbContextAsync();

        var category = await deletedVerificationDb.Categories
            .AsNoTracking()
            .SingleAsync(row => row.Id == categoryId);

        Assert.True(category.IsDeleted);
    }

    [Fact]
    public async Task Management_postbacks_create_update_and_delete_product_with_stock_movements()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await LoginAsAdminAsync(client);

        Guid categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            categoryId = await db.Categories.AsNoTracking().Select(category => category.Id).FirstAsync();
        }

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);

        var createResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Name"] = "Web Managed Product",
                ["Barcode"] = "99001",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "12.50",
                ["Cost"] = "5.25",
                ["InitialStock"] = "4",
                ["ImagePath"] = "images/web-product.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);

        Guid productId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            productId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Web Managed Product" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();
        }

        var updatedPage = await client.GetAsync("/Management");
        var updatedHtml = await ReadHtmlAsync(updatedPage);

        var updateResponse = await client.PostAsync(
            "/Management/UpdateProduct",
            BuildFormContent(updatedHtml, new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["Name"] = "Web Managed Product Updated",
                ["Barcode"] = "99002",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "15.00",
                ["Cost"] = "6.50",
                ["InitialStock"] = "9",
                ["ImagePath"] = "images/web-product-updated.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, updateResponse.StatusCode);

        var deletePage = await client.GetAsync("/Management");
        var deleteHtml = await ReadHtmlAsync(deletePage);

        var deleteResponse = await client.PostAsync(
            "/Management/DeleteProduct",
            BuildFormContent(deleteHtml, new Dictionary<string, string>
            {
                ["productId"] = productId.ToString()
            }));

        Assert.Equal(HttpStatusCode.Redirect, deleteResponse.StatusCode);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDbFactory = verificationScope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        await using var verificationDb = await verificationDbFactory.CreateDbContextAsync();

        var product = await verificationDb.Products
            .AsNoTracking()
            .SingleAsync(row => row.Id == productId);

        Assert.True(product.IsDeleted);
        Assert.False(product.IsActive);

        var inventory = await verificationDb.Inventories
            .AsNoTracking()
            .SingleAsync(row => row.ProductId == productId && !row.IsDeleted);

        Assert.Equal(9m, inventory.Quantity);

        var movements = await verificationDb.StockMovements
            .AsNoTracking()
            .Where(row => row.ProductId == productId && !row.IsDeleted)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync();

        Assert.Collection(
            movements,
            first =>
            {
                Assert.Equal("PRODUCT_CREATE", first.Reference);
                Assert.Equal(4m, first.QuantityAfter);
            },
            second =>
            {
                Assert.Equal("PRODUCT_EDIT", second.Reference);
                Assert.Equal(5m, second.QuantityDelta);
                Assert.Equal(9m, second.QuantityAfter);
            });
    }

    [Fact]
    public async Task Management_get_stock_ledger_filter_limits_rows_to_selected_product()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await LoginAsAdminAsync(client);

        Guid categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            categoryId = await db.Categories.AsNoTracking().Select(category => category.Id).FirstAsync();
        }

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);

        var createTargetResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger Filter Target",
                ["Barcode"] = "99201",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "10.00",
                ["Cost"] = "4.00",
                ["InitialStock"] = "2",
                ["ImagePath"] = "images/ledger-target.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createTargetResponse.StatusCode);

        var secondPage = await client.GetAsync("/Management");
        var secondHtml = await ReadHtmlAsync(secondPage);

        var createOtherResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(secondHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger Filter Other",
                ["Barcode"] = "99202",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "11.00",
                ["Cost"] = "5.00",
                ["InitialStock"] = "6",
                ["ImagePath"] = "images/ledger-other.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createOtherResponse.StatusCode);

        Guid targetProductId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            targetProductId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger Filter Target" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();
        }

        var filteredPage = await client.GetAsync($"/Management?stockProductId={targetProductId}");
        var filteredHtml = await ReadHtmlAsync(filteredPage);

        Assert.Contains("Showing latest movements for Ledger Filter Target.", filteredHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger Filter Target</div>",
            filteredHtml);
        Assert.DoesNotMatch(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger Filter Other</div>",
            filteredHtml);
    }

    [Fact]
    public async Task Management_get_stock_ledger_filter_limits_rows_to_selected_product_and_movement_type()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await LoginAsAdminAsync(client);

        Guid categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            categoryId = await db.Categories.AsNoTracking().Select(category => category.Id).FirstAsync();
        }

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);

        var createResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger Type Target",
                ["Barcode"] = "99301",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "10.00",
                ["Cost"] = "4.00",
                ["InitialStock"] = "2",
                ["ImagePath"] = "images/ledger-type-target.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);

        Guid productId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            productId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger Type Target" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();
        }

        var updatePage = await client.GetAsync("/Management");
        var updateHtml = await ReadHtmlAsync(updatePage);

        var updateResponse = await client.PostAsync(
            "/Management/UpdateProduct",
            BuildFormContent(updateHtml, new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["Name"] = "Ledger Type Target",
                ["Barcode"] = "99301",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "10.00",
                ["Cost"] = "4.00",
                ["InitialStock"] = "5",
                ["ImagePath"] = "images/ledger-type-target.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, updateResponse.StatusCode);

        var filteredPage = await client.GetAsync($"/Management?stockProductId={productId}&stockMovementType=ManualSetAdjustment");
        var filteredHtml = await ReadHtmlAsync(filteredPage);

        Assert.Contains("Showing latest manual-adjustment movements for Ledger Type Target.", filteredHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PRODUCT_EDIT", filteredHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRODUCT_CREATE", filteredHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Management_get_stock_ledger_filter_limits_rows_to_selected_date_range()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await LoginAsAdminAsync(client);

        Guid categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            categoryId = await db.Categories.AsNoTracking().Select(category => category.Id).FirstAsync();
        }

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);

        var createResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger Date Target",
                ["Barcode"] = "99401",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "10.00",
                ["Cost"] = "4.00",
                ["InitialStock"] = "2",
                ["ImagePath"] = "images/ledger-date-target.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);

        Guid productId;
        DateOnly movementDate;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            productId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger Date Target" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();

            var createdAt = await db.StockMovements
                .AsNoTracking()
                .Where(movement => movement.ProductId == productId && !movement.IsDeleted)
                .OrderByDescending(movement => movement.CreatedAt)
                .Select(movement => movement.CreatedAt)
                .FirstAsync();

            movementDate = DateOnly.FromDateTime(createdAt.ToLocalTime());
        }

        var matchingDate = movementDate.ToString("yyyy-MM-dd");
        var matchingPage = await client.GetAsync($"/Management?stockProductId={productId}&stockFromDate={matchingDate}&stockToDate={matchingDate}");
        var matchingHtml = await ReadHtmlAsync(matchingPage);

        Assert.Contains($"Showing latest movements for Ledger Date Target on {matchingDate}.", matchingHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ledger Date Target", matchingHtml, StringComparison.OrdinalIgnoreCase);

        var emptyDate = movementDate.AddDays(-2).ToString("yyyy-MM-dd");
        var emptyPage = await client.GetAsync($"/Management?stockProductId={productId}&stockFromDate={emptyDate}&stockToDate={emptyDate}");
        var emptyHtml = await ReadHtmlAsync(emptyPage);

        Assert.Contains($"Showing latest movements for Ledger Date Target on {emptyDate}.", emptyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No stock movements have been recorded yet.", emptyHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Management_get_stock_ledger_filter_supports_today_preset()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await LoginAsAdminAsync(client);

        Guid categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            categoryId = await db.Categories.AsNoTracking().Select(category => category.Id).FirstAsync();
        }

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);

        var createOldResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger Preset Old",
                ["Barcode"] = "99501",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "10.00",
                ["Cost"] = "4.00",
                ["InitialStock"] = "2",
                ["ImagePath"] = "images/ledger-preset-old.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createOldResponse.StatusCode);

        var secondPage = await client.GetAsync("/Management");
        var secondHtml = await ReadHtmlAsync(secondPage);

        var createTodayResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(secondHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger Preset Today",
                ["Barcode"] = "99502",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "11.00",
                ["Cost"] = "5.00",
                ["InitialStock"] = "3",
                ["ImagePath"] = "images/ledger-preset-today.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createTodayResponse.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            var oldProductId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger Preset Old" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();

            var oldMovements = await db.StockMovements
                .Where(movement => movement.ProductId == oldProductId && !movement.IsDeleted)
                .ToListAsync();

            foreach (var movement in oldMovements)
            {
                movement.CreatedAt = DateTime.UtcNow.Date.AddDays(-1).AddHours(12);
                movement.UpdatedAt = movement.CreatedAt;
            }

            await db.SaveChangesAsync();
        }

        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        var filteredPage = await client.GetAsync("/Management?stockDatePreset=Today");
        var filteredHtml = await ReadHtmlAsync(filteredPage);

        Assert.Contains($"Showing latest movements for all products on {today}.", filteredHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger Preset Today</div>",
            filteredHtml);
        Assert.DoesNotMatch(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger Preset Old</div>",
            filteredHtml);
    }

    [Fact]
    public async Task Management_get_stock_ledger_filter_supports_yesterday_preset()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await LoginAsAdminAsync(client);

        Guid categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            categoryId = await db.Categories.AsNoTracking().Select(category => category.Id).FirstAsync();
        }

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);

        var createOldResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger Yesterday Old",
                ["Barcode"] = "99511",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "10.00",
                ["Cost"] = "4.00",
                ["InitialStock"] = "2",
                ["ImagePath"] = "images/ledger-yesterday-old.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createOldResponse.StatusCode);

        var secondPage = await client.GetAsync("/Management");
        var secondHtml = await ReadHtmlAsync(secondPage);

        var createYesterdayResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(secondHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger Yesterday Match",
                ["Barcode"] = "99512",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "11.00",
                ["Cost"] = "5.00",
                ["InitialStock"] = "3",
                ["ImagePath"] = "images/ledger-yesterday-match.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createYesterdayResponse.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            var oldProductId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger Yesterday Old" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();

            var matchingProductId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger Yesterday Match" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();

            var oldMovements = await db.StockMovements
                .Where(movement => movement.ProductId == oldProductId && !movement.IsDeleted)
                .ToListAsync();

            foreach (var movement in oldMovements)
            {
                movement.CreatedAt = DateTime.UtcNow.Date.AddDays(-5).AddHours(11);
                movement.UpdatedAt = movement.CreatedAt;
            }

            var matchingMovements = await db.StockMovements
                .Where(movement => movement.ProductId == matchingProductId && !movement.IsDeleted)
                .ToListAsync();

            foreach (var movement in matchingMovements)
            {
                movement.CreatedAt = DateTime.UtcNow.Date.AddDays(-1).AddHours(12);
                movement.UpdatedAt = movement.CreatedAt;
            }

            await db.SaveChangesAsync();
        }

        var yesterday = DateOnly.FromDateTime(DateTime.Now).AddDays(-1).ToString("yyyy-MM-dd");
        var filteredPage = await client.GetAsync("/Management?stockDatePreset=Yesterday");
        var filteredHtml = await ReadHtmlAsync(filteredPage);

        Assert.Contains($"Showing latest movements for all products on {yesterday}.", filteredHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger Yesterday Match</div>",
            filteredHtml);
        Assert.DoesNotMatch(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger Yesterday Old</div>",
            filteredHtml);
    }

    [Fact]
    public async Task Management_get_stock_ledger_filter_supports_last_30_days_preset()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await LoginAsAdminAsync(client);

        Guid categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            categoryId = await db.Categories.AsNoTracking().Select(category => category.Id).FirstAsync();
        }

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);

        var createOutOfRangeResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger Last30 Old",
                ["Barcode"] = "99521",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "10.00",
                ["Cost"] = "4.00",
                ["InitialStock"] = "2",
                ["ImagePath"] = "images/ledger-last30-old.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createOutOfRangeResponse.StatusCode);

        var secondPage = await client.GetAsync("/Management");
        var secondHtml = await ReadHtmlAsync(secondPage);

        var createInRangeResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(secondHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger Last30 Match",
                ["Barcode"] = "99522",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "11.00",
                ["Cost"] = "5.00",
                ["InitialStock"] = "3",
                ["ImagePath"] = "images/ledger-last30-match.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createInRangeResponse.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            var outOfRangeProductId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger Last30 Old" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();

            var inRangeProductId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger Last30 Match" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();

            var outOfRangeMovements = await db.StockMovements
                .Where(movement => movement.ProductId == outOfRangeProductId && !movement.IsDeleted)
                .ToListAsync();

            foreach (var movement in outOfRangeMovements)
            {
                movement.CreatedAt = DateTime.UtcNow.Date.AddDays(-40).AddHours(11);
                movement.UpdatedAt = movement.CreatedAt;
            }

            var inRangeMovements = await db.StockMovements
                .Where(movement => movement.ProductId == inRangeProductId && !movement.IsDeleted)
                .ToListAsync();

            foreach (var movement in inRangeMovements)
            {
                movement.CreatedAt = DateTime.UtcNow.Date.AddDays(-20).AddHours(12);
                movement.UpdatedAt = movement.CreatedAt;
            }

            await db.SaveChangesAsync();
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = today.AddDays(-29).ToString("yyyy-MM-dd");
        var toDate = today.ToString("yyyy-MM-dd");
        var filteredPage = await client.GetAsync("/Management?stockDatePreset=Last30Days");
        var filteredHtml = await ReadHtmlAsync(filteredPage);

        Assert.Contains($"Showing latest movements for all products between {fromDate} and {toDate}.", filteredHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger Last30 Match</div>",
            filteredHtml);
        Assert.DoesNotMatch(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger Last30 Old</div>",
            filteredHtml);
    }

    [Fact]
    public async Task Management_get_stock_ledger_filter_supports_this_month_preset_at_month_boundaries()
    {
        using var factory = new WebTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await LoginAsAdminAsync(client);

        Guid categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            categoryId = await db.Categories.AsNoTracking().Select(category => category.Id).FirstAsync();
        }

        var managementPage = await client.GetAsync("/Management");
        var managementHtml = await ReadHtmlAsync(managementPage);

        var createPreviousMonthResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(managementHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger ThisMonth Previous Month",
                ["Barcode"] = "99531",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "10.00",
                ["Cost"] = "4.00",
                ["InitialStock"] = "2",
                ["ImagePath"] = "images/ledger-thismonth-previous-month.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createPreviousMonthResponse.StatusCode);

        var secondPage = await client.GetAsync("/Management");
        var secondHtml = await ReadHtmlAsync(secondPage);

        var createMonthStartResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(secondHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger ThisMonth Month Start",
                ["Barcode"] = "99532",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "11.00",
                ["Cost"] = "5.00",
                ["InitialStock"] = "3",
                ["ImagePath"] = "images/ledger-thismonth-month-start.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createMonthStartResponse.StatusCode);

        var thirdPage = await client.GetAsync("/Management");
        var thirdHtml = await ReadHtmlAsync(thirdPage);

        var createTodayResponse = await client.PostAsync(
            "/Management/CreateProduct",
            BuildFormContent(thirdHtml, new Dictionary<string, string>
            {
                ["Name"] = "Ledger ThisMonth Today",
                ["Barcode"] = "99533",
                ["CategoryId"] = categoryId.ToString(),
                ["Price"] = "12.00",
                ["Cost"] = "6.00",
                ["InitialStock"] = "4",
                ["ImagePath"] = "images/ledger-thismonth-today.png",
                ["IsActive"] = "true"
            }));

        Assert.Equal(HttpStatusCode.Redirect, createTodayResponse.StatusCode);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var previousMonthDay = monthStart.AddDays(-1);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            var previousMonthProductId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger ThisMonth Previous Month" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();

            var monthStartProductId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger ThisMonth Month Start" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();

            var todayProductId = await db.Products
                .AsNoTracking()
                .Where(product => product.Name == "Ledger ThisMonth Today" && !product.IsDeleted)
                .Select(product => product.Id)
                .SingleAsync();

            var previousMonthMovements = await db.StockMovements
                .Where(movement => movement.ProductId == previousMonthProductId && !movement.IsDeleted)
                .ToListAsync();

            foreach (var movement in previousMonthMovements)
            {
                movement.CreatedAt = ToUtcNoon(previousMonthDay);
                movement.UpdatedAt = movement.CreatedAt;
            }

            var monthStartMovements = await db.StockMovements
                .Where(movement => movement.ProductId == monthStartProductId && !movement.IsDeleted)
                .ToListAsync();

            foreach (var movement in monthStartMovements)
            {
                movement.CreatedAt = ToUtcNoon(monthStart);
                movement.UpdatedAt = movement.CreatedAt;
            }

            var todayMovements = await db.StockMovements
                .Where(movement => movement.ProductId == todayProductId && !movement.IsDeleted)
                .ToListAsync();

            foreach (var movement in todayMovements)
            {
                movement.CreatedAt = ToUtcNoon(today);
                movement.UpdatedAt = movement.CreatedAt;
            }

            await db.SaveChangesAsync();
        }

        var filteredPage = await client.GetAsync("/Management?stockDatePreset=ThisMonth");
        var filteredHtml = await ReadHtmlAsync(filteredPage);

        Assert.Contains(
            $"Showing latest movements for all products between {monthStart:yyyy-MM-dd} and {today:yyyy-MM-dd}.",
            filteredHtml,
            StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger ThisMonth Month Start</div>",
            filteredHtml);
        Assert.Matches(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger ThisMonth Today</div>",
            filteredHtml);
        Assert.DoesNotMatch(
            "table-row table-row--stock[\\s\\S]*?<div class=\"table-main\">Ledger ThisMonth Previous Month</div>",
            filteredHtml);
    }

    private static async Task<HttpResponseMessage> LoginAsAdminAsync(HttpClient client) =>
        await LoginAsync(client, "admin", POS.Infrastructure.Data.DatabaseSeeder.DemoAdminPassword);

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password)
    {
        var loginPage = await client.GetAsync("/Account/Login");
        var loginHtml = await ReadHtmlAsync(loginPage);

        return await client.PostAsync(
            "/Account/Login",
            BuildFormContent(loginHtml, new Dictionary<string, string>
            {
                ["Username"] = username,
                ["Password"] = password,
                ["RememberMe"] = "true"
            }));
    }

    private static FormUrlEncodedContent BuildFormContent(string html, IDictionary<string, string> fields)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", ExtractAntiForgeryToken(html))
        };

        values.AddRange(fields.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));
        return new FormUrlEncodedContent(values);
    }

    private static string ExtractAntiForgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"",
            RegexOptions.CultureInvariant);

        Assert.True(match.Success, "Expected antiforgery token input in page markup.");
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    private static async Task<string> ReadHtmlAsync(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect, html);
        return html;
    }

    private static DateTime ToUtcNoon(DateOnly date)
    {
        return DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Local).ToUniversalTime();
    }
}