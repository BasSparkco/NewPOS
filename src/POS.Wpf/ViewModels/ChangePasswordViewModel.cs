using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Wpf.Localization;

namespace POS.Wpf.ViewModels;

public partial class ChangePasswordViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ChangePasswordViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private bool _isBusy;

    public Window? Owner { get; set; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (NewPassword != ConfirmPassword)
        {
            MessageBox.Show(Locale.Get("ChangePassword_Mismatch"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserManagementService>();
            var (success, error) = await users.ChangeOwnPasswordAsync(CurrentPassword, NewPassword);

            if (!success)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (Owner is not null)
                Owner.DialogResult = true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
