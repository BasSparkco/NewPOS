using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Enums;
using POS.Wpf.Localization;

namespace POS.Wpf.ViewModels;

public partial class UserManagementViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    public UserManagementViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [ObservableProperty] private ObservableCollection<UserRowItem> _users = new();
    [ObservableProperty] private ObservableCollection<RoleDto> _roles = new();
    [ObservableProperty] private UserRowItem? _selectedUser;
    [ObservableProperty] private string _editUsername = string.Empty;
    [ObservableProperty] private RoleDto? _editSelectedRole;
    [ObservableProperty] private bool _editIsActive = true;
    [ObservableProperty] private string _newRoleNameText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    // Which role's permission checkboxes are shown — separate from EditSelectedRole above,
    // which only assigns an existing role to a user.
    [ObservableProperty] private RoleDto? _selectedRoleForPermissions;
    [ObservableProperty] private bool _permManageProducts;
    [ObservableProperty] private bool _permViewReports;
    [ObservableProperty] private bool _permViewAudit;
    [ObservableProperty] private bool _permManageUsers;
    [ObservableProperty] private bool _permManageSettings;
    [ObservableProperty] private bool _permProcessRefunds;

    /// <summary>The Admin role always keeps ManageUsers (server-enforced too) so nobody can lock everyone out of this screen.</summary>
    public bool CanEditManageUsersPermission =>
        !string.Equals(SelectedRoleForPermissions?.Name, "Admin", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedUserChanged(UserRowItem? value)
    {
        EditUsername = value?.Username ?? string.Empty;
        EditSelectedRole = value is null ? Roles.FirstOrDefault() : Roles.FirstOrDefault(r => r.Id == value.RoleId);
        EditIsActive = value?.IsActive ?? true;
    }

    partial void OnSelectedRoleForPermissionsChanged(RoleDto? value)
    {
        var mask = value?.PermissionsMask ?? 0;
        PermManageProducts = (mask & (int)Permission.ManageProducts) != 0;
        PermViewReports    = (mask & (int)Permission.ViewReports) != 0;
        PermViewAudit      = (mask & (int)Permission.ViewAudit) != 0;
        PermManageUsers    = (mask & (int)Permission.ManageUsers) != 0;
        PermManageSettings = (mask & (int)Permission.ManageSettings) != 0;
        PermProcessRefunds = (mask & (int)Permission.ProcessRefunds) != 0;
        OnPropertyChanged(nameof(CanEditManageUsersPermission));
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserManagementService>();

            var roles = await users.GetRolesAsync();
            Roles = new ObservableCollection<RoleDto>(roles);

            var rows = await users.GetUsersAsync();
            Users = new ObservableCollection<UserRowItem>(rows.Select(u => new UserRowItem(u.Id, u.Username, u.RoleId, u.RoleName, u.IsActive, u.IsCurrentUser)));

            SelectedUser = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NewUser() => SelectedUser = null;

    [RelayCommand]
    private async Task SaveUserAsync()
    {
        var username = EditUsername.Trim();
        if (username.Length == 0)
        {
            MessageBox.Show(Locale.Get("Users_UsernameRequired"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (EditSelectedRole is null)
        {
            MessageBox.Show(Locale.Get("Users_RoleRequired"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserManagementService>();

            bool success;
            string? error;
            string? generatedPassword = null;

            if (SelectedUser is null)
                (success, error, generatedPassword) = await users.CreateUserAsync(username, EditSelectedRole.Id, EditIsActive);
            else
                (success, error) = await users.UpdateUserAsync(SelectedUser.Id, username, EditSelectedRole.Id, EditIsActive);

            if (!success)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (generatedPassword is not null)
            {
                MessageBox.Show(
                    string.Format(Locale.Get("Users_TemporaryPasswordFormat"), username, generatedPassword),
                    Locale.Get("App_TitleShort"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            StatusText = SelectedUser is null
                ? string.Format(Locale.Get("Users_CreatedFormat"), username)
                : string.Format(Locale.Get("Users_UpdatedFormat"), username);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (SelectedUser is null)
        {
            MessageBox.Show(Locale.Get("Users_SelectUserForPasswordReset"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserManagementService>();
            var username = SelectedUser.Username;
            var (success, error, generatedPassword) = await users.ResetUserPasswordAsync(SelectedUser.Id);

            if (!success || generatedPassword is null)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(
                string.Format(Locale.Get("Users_TemporaryPasswordFormat"), username, generatedPassword),
                Locale.Get("App_TitleShort"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            StatusText = string.Format(Locale.Get("Users_PasswordResetFormat"), username);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddRoleAsync()
    {
        var name = NewRoleNameText.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(Locale.Get("Users_RoleNameRequired"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserManagementService>();
            var (success, error, role) = await users.CreateRoleAsync(name);

            if (!success || role is null)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            NewRoleNameText = string.Empty;
            Roles = new ObservableCollection<RoleDto>(Roles.Append(role).OrderBy(r => r.Name));
            EditSelectedRole = Roles.First(r => r.Id == role.Id);
            SelectedRoleForPermissions = EditSelectedRole;
            StatusText = string.Format(Locale.Get("Users_RoleCreatedFormat"), role.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SavePermissionsAsync()
    {
        if (SelectedRoleForPermissions is null)
        {
            MessageBox.Show(Locale.Get("Users_SelectRoleForPermissions"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var mask = Permission.None;
        if (PermManageProducts) mask |= Permission.ManageProducts;
        if (PermViewReports)    mask |= Permission.ViewReports;
        if (PermViewAudit)      mask |= Permission.ViewAudit;
        if (PermManageUsers)    mask |= Permission.ManageUsers;
        if (PermManageSettings) mask |= Permission.ManageSettings;
        if (PermProcessRefunds) mask |= Permission.ProcessRefunds;

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserManagementService>();
            var roleId = SelectedRoleForPermissions.Id;
            var roleName = SelectedRoleForPermissions.Name;
            var (success, error) = await users.UpdateRolePermissionsAsync(roleId, (int)mask);

            if (!success)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusText = string.Format(Locale.Get("Users_PermissionsSavedFormat"), roleName);
            await LoadAsync();
            SelectedRoleForPermissions = Roles.FirstOrDefault(r => r.Id == roleId);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed record UserRowItem(Guid Id, string Username, Guid RoleId, string RoleName, bool IsActive, bool IsCurrentUser);
