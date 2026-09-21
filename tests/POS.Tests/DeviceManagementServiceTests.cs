using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;

namespace POS.Tests;

public class DeviceManagementServiceTests
{
    [Fact]
    public async Task Provisioning_creates_unenrolled_device_with_working_code()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var devices = services.GetRequiredService<IDeviceManagementService>();

            var (success, error, code) = await devices.ProvisionDeviceAsync("Front Counter");
            Assert.True(success, error);
            Assert.False(string.IsNullOrWhiteSpace(code));

            var list = await devices.GetDevicesAsync();
            var provisioned = Assert.Single(list, d => d.Name == "Front Counter");
            Assert.False(provisioned.IsEnrolled);
            Assert.False(provisioned.IsRevoked);
        });
    }

    [Fact]
    public async Task Enroll_with_wrong_code_fails()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var devices = services.GetRequiredService<IDeviceManagementService>();
            await devices.ProvisionDeviceAsync("Front Counter");

            var (success, error) = await devices.EnrollCurrentMachineAsync("definitely-wrong-code");
            Assert.False(success);
            Assert.NotNull(error);
        });
    }

    [Fact]
    public async Task Enroll_with_correct_code_binds_device_to_current_machine()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var devices = services.GetRequiredService<IDeviceManagementService>();
            var (_, _, code) = await devices.ProvisionDeviceAsync("Front Counter");

            var (success, error) = await devices.EnrollCurrentMachineAsync(code!);
            Assert.True(success, error);

            var list = await devices.GetDevicesAsync();
            var enrolled = Assert.Single(list, d => d.Name == "POS.Tests");
            Assert.True(enrolled.IsEnrolled);
            Assert.True(enrolled.IsCurrentMachine);

            // The code is one-time use.
            var (reuseSuccess, reuseError) = await devices.EnrollCurrentMachineAsync(code!);
            Assert.False(reuseSuccess);
            Assert.NotNull(reuseError);
        });
    }

    [Fact]
    public async Task Sale_is_blocked_when_enrollment_required_and_machine_not_provisioned()
    {
        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?> { ["Sync:RequireDeviceEnrollment"] = "true" });

        await host.ExecuteScopeAsync(async services =>
        {
            var sales = services.GetRequiredService<ISaleService>();
            await Assert.ThrowsAsync<DeviceNotAuthorizedException>(() => sales.StartNewSaleAsync());
        });
    }

    [Fact]
    public async Task Sale_succeeds_after_enrollment_when_enrollment_required()
    {
        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?> { ["Sync:RequireDeviceEnrollment"] = "true" });

        await host.ExecuteScopeAsync(async services =>
        {
            var devices = services.GetRequiredService<IDeviceManagementService>();
            var (_, _, code) = await devices.ProvisionDeviceAsync("Front Counter");
            var (enrollSuccess, enrollError) = await devices.EnrollCurrentMachineAsync(code!);
            Assert.True(enrollSuccess, enrollError);

            var sales = services.GetRequiredService<ISaleService>();
            var invoiceId = await sales.StartNewSaleAsync();
            Assert.NotEqual(Guid.Empty, invoiceId);
        });
    }

    [Fact]
    public async Task Revoked_device_blocks_sales_even_without_enrollment_requirement()
    {
        // Default lenient host — enrollment is NOT required, matching today's out-of-the-box behavior.
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var sales = services.GetRequiredService<ISaleService>();
            var devices = services.GetRequiredService<IDeviceManagementService>();

            // Auto-creates a lenient, already-trusted device named "POS.Tests".
            await sales.StartNewSaleAsync();

            var list = await devices.GetDevicesAsync();
            var device = Assert.Single(list, d => d.Name == "POS.Tests");

            var (revokeSuccess, revokeError) = await devices.RevokeDeviceAsync(device.Id);
            Assert.True(revokeSuccess, revokeError);

            await Assert.ThrowsAsync<DeviceNotAuthorizedException>(() => sales.StartNewSaleAsync());
        });
    }
}
