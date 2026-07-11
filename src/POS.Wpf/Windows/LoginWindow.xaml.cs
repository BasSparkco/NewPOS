using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using POS.Wpf.Localization;
using POS.Wpf.ViewModels;

namespace POS.Wpf.Windows;

public partial class LoginWindow : Window
{
    public LoginWindow(LoginViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.Owner = this;
        InitializeComponent();
        ApplyLocalization();
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        Title              = Locale.Get("Login_Title");
        LoginUserLabel.Text = Locale.Get("Login_Username");
        LoginSignInBtn.Content = Locale.Get("Login_SignIn");
    }

    private async void LoginSignInBtn_OnClick(object sender, RoutedEventArgs e)
    {
        SyncCredentialsToViewModel();

        if (DataContext is LoginViewModel vm)
            await vm.LoginCommand.ExecuteAsync(null);
    }

    private void SyncCredentialsToViewModel()
    {
        if (DataContext is not LoginViewModel vm)
            return;

        vm.Username = UsernameBox.Text;

        var binding = UsernameBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
        binding?.UpdateSource();
    }
}
