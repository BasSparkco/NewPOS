using System.Globalization;
using System.Windows;
using POS.Wpf.Localization;

namespace POS.Wpf.Windows;

public partial class PaymentWindow : Window
{
    private readonly decimal _totalDue;
    private readonly string? _currencySuffix;

    public decimal CashTendered { get; private set; }

    public PaymentWindow(decimal totalDue, string? currencySuffix = null)
    {
        InitializeComponent();
        _totalDue        = totalDue;
        _currencySuffix  = string.IsNullOrWhiteSpace(currencySuffix) ? null : currencySuffix.Trim();
        ApplyLocalization();
        TotalLabel.Text  = FormatMoney(totalDue);
        ChangeLabel.Text = FormatMoney(0m);
        Loaded += (_, _) => CashBox.Focus();
    }

    private string FormatMoney(decimal amount) =>
        amount.ToString("N2", CultureInfo.CurrentCulture)
        + (_currencySuffix is null ? "" : " " + _currencySuffix);

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        Title                       = Locale.Get("Payment_Title");
        PaymentHeaderTb.Text        = Locale.Get("Payment_Header");
        PaymentTotalDueTb.Text      = Locale.Get("Payment_TotalDue");
        PaymentCashRecvTb.Text      = Locale.Get("Payment_CashReceived");
        PaymentChangeCaptionTb.Text = Locale.Get("Payment_Change");
        PaymentCancelBtn.Content    = Locale.Get("Payment_Cancel");
        PaymentOkBtn.Content        = Locale.Get("Payment_Complete");
    }

    private void CashBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (decimal.TryParse(CashBox.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var received))
        {
            var change = received - _totalDue;
            ChangeLabel.Text = change >= 0 ? FormatMoney(change) : "—";
        }
        else
        {
            ChangeLabel.Text = "—";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(CashBox.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var received)
            || received < _totalDue)
        {
            MessageBox.Show(Locale.Get("Payment_CashMustCover"), Locale.Get("App_TitleShort"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        CashTendered = received;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
