using System.Windows;
using POS.Wpf.Localization;
using POS.Wpf.ViewModels;

namespace POS.Wpf.Windows;

public partial class SetupWindow : Window
{
    public SetupWindow(SetupViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.Owner = this;
        InitializeComponent();
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        Title = Locale.Get("Setup_Title");
        SetupTitleTb.Text = Locale.Get("Setup_Title");
        SetupIntroTb.Text = Locale.Get("Setup_Intro");
        StartFreshBtn.Content = Locale.Get("Setup_StartFreshButton");
        ShowJoinBtn.Content = Locale.Get("Setup_JoinButton");
        ApiUrlLbl.Text = Locale.Get("Setup_Join_ApiUrlLabel");
        SlugLbl.Text = Locale.Get("Setup_Join_SlugLabel");
        UsernameLbl.Text = Locale.Get("Setup_Join_UsernameLabel");
        PasswordLbl.Text = Locale.Get("Setup_Join_PasswordLabel");
        JoinSubmitBtn.Content = Locale.Get("Setup_Join_SubmitButton");
        BackBtn.Content = Locale.Get("Setup_BackButton");
    }

    private async void JoinSubmitBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SetupViewModel vm)
            return;

        vm.Password = PasswordBox.Password;
        await vm.JoinCommand.ExecuteAsync(null);
    }
}
