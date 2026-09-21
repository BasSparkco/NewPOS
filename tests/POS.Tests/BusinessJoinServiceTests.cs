using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using POS.Application.Abstractions;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class BusinessJoinServiceTests
{
    [Fact]
    public async Task JoinExistingBusinessAsync_bootstraps_local_database_and_signs_in_locally()
    {
        using var remote = new ApiTestFactory();

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:BatchSize"] = "50"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new RemoteApiHttpClientFactory(remote));
            },
            seedBaseline: false);

        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            // A fresh install's own migrations already insert exactly one placeholder "bootstrap
            // tenant" row (to backfill any pre-existing single-tenant data) — Users, not Tenants, is
            // the real "has this device ever been configured" signal.
            await using (var preCheckDb = await dbFactory.CreateDbContextAsync())
                Assert.False(await preCheckDb.Users.AnyAsync());

            var join = services.GetRequiredService<IBusinessJoinService>();
            var (success, error) = await join.JoinExistingBusinessAsync(
                "http://localhost",
                "default",
                "admin",
                POS.Infrastructure.Data.DatabaseSeeder.DemoAdminPassword);

            Assert.True(success, error);
            Assert.Null(error);

            await using var db = await dbFactory.CreateDbContextAsync();

            var tenant = await db.Tenants.AsNoTracking().SingleAsync();
            Assert.Equal("default", tenant.NormalizedSlug);

            var store = await db.Stores.AsNoTracking().SingleAsync();
            Assert.Equal(tenant.Id, store.TenantId);

            var localAdmin = await db.Users.AsNoTracking().SingleAsync(u => u.Username == "admin");
            Assert.True(BCrypt.Net.BCrypt.Verify(POS.Infrastructure.Data.DatabaseSeeder.DemoAdminPassword, localAdmin.PasswordHash));

            var role = await db.Roles.AsNoTracking().SingleAsync(r => r.Id == localAdmin.RoleId);
            Assert.Equal("Admin", role.Name);

            Assert.True(await db.Categories.AsNoTracking().AnyAsync(c => c.Name == "General"));
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.Name == "Sample Item A");
            Assert.True(await db.Inventories.AsNoTracking().AnyAsync(i => i.ProductId == product.Id && i.StoreId == store.Id));

            var session = services.GetRequiredService<ICurrentSession>();
            Assert.True(session.IsAuthenticated);
            Assert.Equal("admin", session.Username);
        });
    }

    [Fact]
    public async Task JoinExistingBusinessAsync_fails_cleanly_with_the_wrong_password_and_leaves_the_local_database_fresh()
    {
        using var remote = new ApiTestFactory();

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new RemoteApiHttpClientFactory(remote));
            },
            seedBaseline: false);

        await host.ExecuteScopeAsync(async services =>
        {
            var join = services.GetRequiredService<IBusinessJoinService>();
            var (success, error) = await join.JoinExistingBusinessAsync(
                "http://localhost", "default", "admin", "definitely-the-wrong-password");

            Assert.False(success);
            Assert.NotNull(error);

            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            Assert.False(await db.Users.AnyAsync());

            var session = services.GetRequiredService<ICurrentSession>();
            Assert.False(session.IsAuthenticated);
        });
    }

    [Fact]
    public async Task JoinExistingBusinessAsync_fails_cleanly_when_the_server_is_unreachable()
    {
        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new ThrowingHttpClientFactory());
            },
            seedBaseline: false);

        await host.ExecuteScopeAsync(async services =>
        {
            var join = services.GetRequiredService<IBusinessJoinService>();
            var (success, error) = await join.JoinExistingBusinessAsync(
                "http://unreachable.example.test", "default", "admin", "whatever");

            Assert.False(success);
            Assert.NotNull(error);

            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            Assert.False(await db.Users.AnyAsync());
        });
    }

    private sealed class RemoteApiHttpClientFactory(ApiTestFactory remote) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => remote.CreateClient();
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler());

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("Simulated unreachable server.");
        }
    }
}
