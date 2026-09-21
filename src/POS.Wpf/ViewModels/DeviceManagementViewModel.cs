using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Wpf.Localization;

namespace POS.Wpf.ViewModels;

public partial class DeviceManagementViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DeviceManagementViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [ObservableProperty] private ObservableCollection<DeviceListItemDto> _devices = new();
    [ObservableProperty] private DeviceListItemDto? _selectedDevice;
    [ObservableProperty] private string _newDeviceName = string.Empty;
    [ObservableProperty] private string _enrollmentCodeText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var devices = scope.ServiceProvider.GetRequiredService<IDeviceManagementService>();
            var rows = await devices.GetDevicesAsync();
            Devices = new ObservableCollection<DeviceListItemDto>(rows);
            SelectedDevice = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProvisionAsync()
    {
        var name = NewDeviceName.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(Locale.Get("Devices_NameRequired"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var devices = scope.ServiceProvider.GetRequiredService<IDeviceManagementService>();
            var (success, error, code) = await devices.ProvisionDeviceAsync(name);

            if (!success || code is null)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            NewDeviceName = string.Empty;
            MessageBox.Show(
                string.Format(Locale.Get("Devices_EnrollmentCodeFormat"), name, code),
                Locale.Get("App_TitleShort"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            StatusText = string.Format(Locale.Get("Devices_ProvisionedFormat"), name);
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevokeAsync()
    {
        if (SelectedDevice is null)
        {
            MessageBox.Show(Locale.Get("Devices_SelectToRevoke"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(
                string.Format(Locale.Get("Devices_ConfirmRevokeFormat"), SelectedDevice.Name),
                Locale.Get("App_TitleShort"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var devices = scope.ServiceProvider.GetRequiredService<IDeviceManagementService>();
            var (success, error) = await devices.RevokeDeviceAsync(SelectedDevice.Id);

            if (!success)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusText = string.Format(Locale.Get("Devices_RevokedFormat"), SelectedDevice.Name);
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnrollThisMachineAsync()
    {
        var code = EnrollmentCodeText.Trim();
        if (code.Length == 0)
        {
            MessageBox.Show(Locale.Get("Devices_CodeRequired"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var devices = scope.ServiceProvider.GetRequiredService<IDeviceManagementService>();
            var (success, error) = await devices.EnrollCurrentMachineAsync(code);

            if (!success)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            EnrollmentCodeText = string.Empty;
            StatusText = Locale.Get("Devices_EnrolledThisMachine");
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
