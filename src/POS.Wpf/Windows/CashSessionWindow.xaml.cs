using System.Windows;
using POS.Wpf.Localization;
using POS.Wpf.ViewModels;

namespace POS.Wpf.Windows;

public partial class CashSessionWindow : Window
{
    public CashSessionWindow(CashSessionViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ApplyLocalization();

        if (DataContext is CashSessionViewModel vm)
            await vm.LoadAsync();
    }

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        Title = Locale.Get("CashSession_Title");
        HeaderTb.Text = Locale.Get("CashSession_Header");

        SummaryHeaderTb.Text = Locale.Get("CashSession_SummaryHeader");
        NoRegisterTb.Text = Locale.Get("CashSession_NoRegister");

        OpenHeaderTb.Text = Locale.Get("CashSession_OpenHeader");
        OpenHintTb.Text = Locale.Get("CashSession_OpenHint");
        OpeningAmountLbl.Text = Locale.Get("CashSession_OpeningAmountLabel");
        OpenBtn.Content = Locale.Get("CashSession_OpenButton");

        ManualHeaderTb.Text = Locale.Get("CashSession_ManualHeader");
        ManualAmountLbl.Text = Locale.Get("CashSession_AmountLabel");
        ManualNotesLbl.Text = Locale.Get("CashSession_NotesLabel");
        ApproverLbl.Text = Locale.Get("CashSession_ApproverLabel");
        CashInBtn.Content = Locale.Get("CashSession_CashInButton");
        CashOutBtn.Content = Locale.Get("CashSession_CashOutButton");

        CloseHeaderTb.Text = Locale.Get("CashSession_CloseHeader");
        CloseHintTb.Text = Locale.Get("CashSession_CloseHint");
        ClosingAmountLbl.Text = Locale.Get("CashSession_ClosingAmountLabel");
        ClosingNotesLbl.Text = Locale.Get("CashSession_NotesLabel");
        CloseSessionBtn.Content = Locale.Get("CashSession_CloseButton");

        WindowCloseBtn.Content = Locale.Get("Devices_Close");

        ColTime.Header = Locale.Get("CashSession_ColTime");
        ColType.Header = Locale.Get("CashSession_ColType");
        ColAmount.Header = Locale.Get("CashSession_ColAmount");
        ColMethod.Header = Locale.Get("CashSession_ColMethod");
        ColUser.Header = Locale.Get("CashSession_ColUser");
        ColNotes.Header = Locale.Get("CashSession_ColNotes");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
