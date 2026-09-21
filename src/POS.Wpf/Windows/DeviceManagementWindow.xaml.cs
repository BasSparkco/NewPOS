using System.Windows;
using POS.Wpf.Localization;
using POS.Wpf.ViewModels;

namespace POS.Wpf.Windows;

public partial class DeviceManagementWindow : Window
{
    public DeviceManagementWindow(DeviceManagementViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ApplyLocalization();

        if (DataContext is DeviceManagementViewModel vm)
            await vm.LoadAsync();
    }

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        Title = Locale.Get("Devices_Title");
        DevicesHeaderTb.Text = Locale.Get("Devices_Header");
        DevicesSubtitleTb.Text = Locale.Get("Devices_Subtitle");

        DevicesProvisionHeaderTb.Text = Locale.Get("Devices_ProvisionHeader");
        DevicesProvisionHintTb.Text = Locale.Get("Devices_ProvisionHint");
        DevicesNameLbl.Text = Locale.Get("Devices_NameLabel");
        DevicesProvisionBtn.Content = Locale.Get("Devices_ProvisionButton");

        DevicesRevokeHeaderTb.Text = Locale.Get("Devices_RevokeHeader");
        DevicesRevokeHintTb.Text = Locale.Get("Devices_RevokeHint");
        DevicesRevokeBtn.Content = Locale.Get("Devices_RevokeButton");

        DevicesEnrollHeaderTb.Text = Locale.Get("Devices_EnrollHeader");
        DevicesEnrollHintTb.Text = Locale.Get("Devices_EnrollHint");
        DevicesEnrollBtn.Content = Locale.Get("Devices_EnrollButton");

        DevicesCloseBtn.Content = Locale.Get("Devices_Close");

        DevicesNameCol.Header = Locale.Get("Devices_ColName");
        DevicesEnrolledCol.Header = Locale.Get("Devices_ColEnrolled");
        DevicesRevokedCol.Header = Locale.Get("Devices_ColRevoked");
        DevicesThisMachineCol.Header = Locale.Get("Devices_ColThisMachine");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
