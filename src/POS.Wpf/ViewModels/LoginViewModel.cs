using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Wpf.Localization;

namespace POS.Wpf.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    public LoginViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    public Window? Owner { get; set; }

    [RelayCommand]
    private async Task LoginAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var result = await auth.LoginAsync(Username, Password);
        if (!result.Success)
        {
            MessageBox.Show(result.ErrorMessage ?? Locale.Get("Login_Failed"), Locale.Get("Login_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Owner is not null)
            Owner.DialogResult = true;
    }
}
