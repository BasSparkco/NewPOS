using System.Windows;
using POS.Wpf.Localization;
using POS.Wpf.ViewModels;

namespace POS.Wpf.Windows;

public partial class ChangePasswordWindow : Window
{
    public ChangePasswordWindow(ChangePasswordViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.Owner = this;
        InitializeComponent();
        ApplyLocalization();
        Loaded += (_, _) => CurrentPasswordBox.Focus();
    }

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        Title = Locale.Get("ChangePassword_Title");
        CurrentPasswordLabel.Text = Locale.Get("ChangePassword_CurrentPassword");
        NewPasswordLabel.Text = Locale.Get("ChangePassword_NewPassword");
        ConfirmPasswordLabel.Text = Locale.Get("ChangePassword_ConfirmPassword");
        CancelBtn.Content = Locale.Get("ChangePassword_Cancel");
        SaveBtn.Content = Locale.Get("ChangePassword_Save");
    }

    private async void SaveBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChangePasswordViewModel vm)
            return;

        vm.CurrentPassword = CurrentPasswordBox.Password;
        vm.NewPassword = NewPasswordBox.Password;
        vm.ConfirmPassword = ConfirmPasswordBox.Password;

        await vm.SaveCommand.ExecuteAsync(null);
    }

    private void CancelBtn_OnClick(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
