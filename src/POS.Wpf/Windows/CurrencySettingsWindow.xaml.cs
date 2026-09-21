using System.Windows;
using POS.Wpf.Localization;
using POS.Wpf.ViewModels;

namespace POS.Wpf.Windows;

public partial class CurrencySettingsWindow : Window
{
    public CurrencySettingsWindow(CurrencySettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Locale.ApplyFlowDirection(this);
        Title = Locale.Get("CurrencySettings_Title");
        CurrencyHeaderTb.Text = Locale.Get("CurrencySettings_Header");
        CurrencySubtitleTb.Text = Locale.Get("CurrencySettings_Subtitle");
        OperationalHeaderTb.Text = Locale.Get("CurrencySettings_OperationalHeader");
        OperationalSubtitleTb.Text = Locale.Get("CurrencySettings_OperationalSubtitle");
        AllowNegativeStockCheck.Content = Locale.Get("CurrencySettings_AllowNegativeStock");
        UseArabicIndicDigitsCheck.Content = Locale.Get("CurrencySettings_UseArabicIndicDigits");
        LowStockThresholdLbl.Text = Locale.Get("CurrencySettings_LowStockThreshold");
        DefaultTaxPercentLbl.Text = Locale.Get("CurrencySettings_DefaultTaxPercent");
        PricesIncludeVatCheck.Content = Locale.Get("CurrencySettings_PricesIncludeVat");
        ReceiptFooterLbl.Text = Locale.Get("CurrencySettings_ReceiptFooter");
        ExchangeRatesHeaderTb.Text = Locale.Get("CurrencySettings_ExchangeRatesHeader");
        CurrencyStoreLbl.Text = Locale.Get("CurrencySettings_Store");
        CurrencyBaseLbl.Text = Locale.Get("CurrencySettings_BaseCurrency");
        CurrencyHintTb.Text = Locale.Get("CurrencySettings_BaseHint");
        CurrencyCancelBtn.Content = Locale.Get("CurrencySettings_Cancel");
        CurrencySaveBtn.Content = Locale.Get("CurrencySettings_Save");

        if (CurrenciesGrid.Columns.Count >= 4)
        {
            CurrenciesGrid.Columns[0].Header = Locale.Get("CurrencySettings_Code");
            CurrenciesGrid.Columns[1].Header = Locale.Get("CurrencySettings_Name");
            CurrenciesGrid.Columns[2].Header = Locale.Get("CurrencySettings_Symbol");
            CurrenciesGrid.Columns[3].Header = Locale.Get("CurrencySettings_ExchangeRate");
        }

        if (DataContext is CurrencySettingsViewModel vm)
            await vm.LoadAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}