using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Core.Enums;

namespace POS.Tests;

public class UserManagementServiceTests
{
    [Fact]
    public async Task Create_user_appears_in_list_with_assigned_role()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var users = services.GetRequiredService<IUserManagementService>();
            var roles = await users.GetRolesAsync();
            var adminRole = Assert.Single(roles);

            var (success, error, generatedPassword) = await users.CreateUserAsync("cashier1", adminRole.Id, isActive: true);
            Assert.True(success, error);
            Assert.False(string.IsNullOrWhiteSpace(generatedPassword));

            var list = await users.GetUsersAsync();
            var created = Assert.Single(list, u => u.Username == "cashier1");
            Assert.Equal(adminRole.Id, created.RoleId);
            Assert.True(created.IsActive);
        });
    }

    [Fact]
    public async Task Create_user_rejects_duplicate_username_in_store()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var users = services.GetRequiredService<IUserManagementService>();
            var role = Assert.Single(await users.GetRolesAsync());

            await users.CreateUserAsync("dupe", role.Id, true);
            var (success, error, _) = await users.CreateUserAsync("DUPE", role.Id, true);

            Assert.False(success);
            Assert.NotNull(error);
        });
    }

    [Fact]
    public async Task Update_user_cannot_deactivate_the_current_session_account()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var users = services.GetRequiredService<IUserManagementService>();
            var role = Assert.Single(await users.GetRolesAsync());

            var (success, error) = await users.UpdateUserAsync(host.UserId, "admin", role.Id, isActive: false);

            Assert.False(success);
            Assert.NotNull(error);
        });
    }

    [Fact]
    public async Task Create_role_then_update_permissions_round_trips()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var users = services.GetRequiredService<IUserManagementService>();

            var (success, error, role) = await users.CreateRoleAsync("Supervisor", (int)Permission.None);
            Assert.True(success, error);
            Assert.NotNull(role);

            var grant = Permission.ViewReports | Permission.ProcessRefunds;
            var (updated, updateError) = await users.UpdateRolePermissionsAsync(role!.Id, (int)grant);
            Assert.True(updated, updateError);

            var reloaded = (await users.GetRolesAsync()).Single(r => r.Id == role.Id);
            Assert.Equal((int)grant, reloaded.PermissionsMask);
        });
    }

    [Fact]
    public async Task Admin_role_permissions_always_keep_ManageUsers()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var users = services.GetRequiredService<IUserManagementService>();
            var adminRole = Assert.Single(await users.GetRolesAsync());

            var (success, error) = await users.UpdateRolePermissionsAsync(adminRole.Id, (int)Permission.None);
            Assert.True(success, error);

            var reloaded = (await users.GetRolesAsync()).Single(r => r.Id == adminRole.Id);
            Assert.True(((Permission)reloaded.PermissionsMask).HasFlag(Permission.ManageUsers));
        });
    }
}
