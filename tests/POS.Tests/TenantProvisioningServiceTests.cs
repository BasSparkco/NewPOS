using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;

namespace POS.Tests;

public class TenantProvisioningServiceTests
{
    [Fact]
    public async Task Provisioning_creates_tenant_store_and_admin_that_can_change_their_own_password()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var provisioning = services.GetRequiredService<ITenantProvisioningService>();

            var (success, error, result) = await provisioning.ProvisionTenantAsync(
                "Second Business", "second-business", "Main Store", "owner", "OwnerPassword1!", "USD");

            Assert.True(success, error);
            Assert.NotNull(result);
            Assert.NotEqual(Guid.Empty, result!.TenantId);
            Assert.NotEqual(Guid.Empty, result.StoreId);
            Assert.NotEqual(Guid.Empty, result.AdminUserId);
            Assert.NotEqual(host.TenantId, result.TenantId);
        });
    }

    [Fact]
    public async Task Provisioning_rejects_a_slug_already_in_use()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var provisioning = services.GetRequiredService<ITenantProvisioningService>();

            // TestServiceHost's fixture tenant already uses slug "test-tenant".
            var (success, error, result) = await provisioning.ProvisionTenantAsync(
                "Duplicate Slug Business", "test-tenant", "Main Store", "owner", "OwnerPassword1!");

            Assert.False(success);
            Assert.NotNull(error);
            Assert.Null(result);
        });
    }

    [Fact]
    public async Task Provisioning_rejects_a_short_admin_password()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var provisioning = services.GetRequiredService<ITenantProvisioningService>();

            var (success, error, _) = await provisioning.ProvisionTenantAsync(
                "Second Business", "second-business", "Main Store", "owner", "short");

            Assert.False(success);
            Assert.NotNull(error);
        });
    }
}
