using System.Windows;
using POS.Wpf.Localization;
using POS.Wpf.ViewModels;

namespace POS.Wpf.Windows;

public partial class UserManagementWindow : Window
{
    public UserManagementWindow(UserManagementViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ApplyLocalization();

        if (DataContext is UserManagementViewModel vm)
            await vm.LoadAsync();
    }

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        Title = Locale.Get("Users_Title");
        UsersHeaderTb.Text = Locale.Get("Users_Header");
        UsersSubtitleTb.Text = Locale.Get("Users_Subtitle");
        UsersFormHeaderTb.Text = Locale.Get("Users_FormHeader");
        UsersUsernameLbl.Text = Locale.Get("Users_Username");
        UsersRoleLbl.Text = Locale.Get("Users_Role");
        UsersActiveCheck.Content = Locale.Get("Users_Active");
        UsersSaveBtn.Content = Locale.Get("Users_Save");
        UsersNewBtn.Content = Locale.Get("Users_New");
        UsersNewRoleHeaderTb.Text = Locale.Get("Users_NewRoleHeader");
        UsersNewRoleHintTb.Text = Locale.Get("Users_NewRoleHint");
        UsersAddRoleBtn.Content = Locale.Get("Users_AddRole");
        UsersCloseBtn.Content = Locale.Get("Users_Close");

        UsersPermissionsHeaderTb.Text = Locale.Get("Users_PermissionsHeader");
        UsersPermissionsHintTb.Text = Locale.Get("Users_PermissionsHint");
        UsersPermManageProductsCheck.Content = Locale.Get("Users_PermManageProducts");
        UsersPermViewReportsCheck.Content = Locale.Get("Users_PermViewReports");
        UsersPermViewAuditCheck.Content = Locale.Get("Users_PermViewAudit");
        UsersPermManageUsersCheck.Content = Locale.Get("Users_PermManageUsers");
        UsersPermManageSettingsCheck.Content = Locale.Get("Users_PermManageSettings");
        UsersPermProcessRefundsCheck.Content = Locale.Get("Users_PermProcessRefunds");
        UsersSavePermissionsBtn.Content = Locale.Get("Users_SavePermissions");

        UsersUsernameCol.Header = Locale.Get("Users_ColUsername");
        UsersRoleCol.Header = Locale.Get("Users_ColRole");
        UsersActiveCol.Header = Locale.Get("Users_ColActive");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
