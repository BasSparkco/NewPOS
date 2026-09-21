using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Core.Enums;
using POS.Infrastructure.Data;

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
    public async Task ChangeOwnPassword_succeeds_with_correct_current_password_and_rejects_wrong_one()
    {
        await using var host = await TestServiceHost.CreateAsync();

        const string currentPassword = "CurrentPw1!";
        const string newPassword = "NewPassword2@";

        // Seeded fixture user has a placeholder, non-BCrypt PasswordHash — give it a real one first.
        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.SingleAsync(u => u.Id == host.UserId);
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(currentPassword);
            await db.SaveChangesAsync();
        });

        await host.ExecuteScopeAsync(async services =>
        {
            var users = services.GetRequiredService<IUserManagementService>();

            var (wrongSuccess, wrongError) = await users.ChangeOwnPasswordAsync("not-the-password", newPassword);
            Assert.False(wrongSuccess);
            Assert.NotNull(wrongError);

            var (success, error) = await users.ChangeOwnPasswordAsync(currentPassword, newPassword);
            Assert.True(success, error);
        });

        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.SingleAsync(u => u.Id == host.UserId);
            Assert.True(BCrypt.Net.BCrypt.Verify(newPassword, user.PasswordHash));
        });
    }

    [Fact]
    public async Task ChangeOwnPassword_rejects_new_password_shorter_than_8_characters()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.SingleAsync(u => u.Id == host.UserId);
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword("CurrentPw1!");
            await db.SaveChangesAsync();
        });

        await host.ExecuteScopeAsync(async services =>
        {
            var users = services.GetRequiredService<IUserManagementService>();
            var (success, error) = await users.ChangeOwnPasswordAsync("CurrentPw1!", "short");
            Assert.False(success);
            Assert.NotNull(error);
        });
    }

    [Fact]
    public async Task ResetUserPassword_generates_a_new_password_that_verifies_against_the_stored_hash()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var users = services.GetRequiredService<IUserManagementService>();
            var role = Assert.Single(await users.GetRolesAsync());
            var (createSuccess, createError, _) = await users.CreateUserAsync("resettarget", role.Id, true);
            Assert.True(createSuccess, createError);

            var created = (await users.GetUsersAsync()).Single(u => u.Username == "resettarget");
            var (success, error, generatedPassword) = await users.ResetUserPasswordAsync(created.Id);
            Assert.True(success, error);
            Assert.False(string.IsNullOrWhiteSpace(generatedPassword));

            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var reloaded = await db.Users.SingleAsync(u => u.Id == created.Id);
            Assert.True(BCrypt.Net.BCrypt.Verify(generatedPassword, reloaded.PasswordHash));
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
