using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using POS.Application.Models;
using POS.Wpf.Localization;

namespace POS.Wpf.Windows;

public partial class CustomerDisplayWindow : Window
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public CustomerDisplayWindow()
    {
        InitializeComponent();
        Locale.ApplyFlowDirection(this);
        Title            = Locale.Get("CustomerDisplay_Title");
        CdWelcomeTb.Text = Locale.Get("CustomerDisplay_Welcome");
        CdSubtitleTb.Text = Locale.Get("CustomerDisplay_Subtitle");
        CdTotalLabelTb.Text = Locale.Get("CustomerDisplay_Total");
        CdSubtotalLabelTb.Text = Locale.Get("CustomerDisplay_Subtotal");
        HeaderProductTb.Text = Locale.Get("CustomerDisplay_HeaderProduct");
        HeaderPriceTb.Text   = Locale.Get("CustomerDisplay_HeaderPrice");
        HeaderQtyTb.Text     = Locale.Get("CustomerDisplay_HeaderQty");
        HeaderSumTb.Text     = Locale.Get("CustomerDisplay_HeaderSum");
        _clock.Tick += (_, _) => TimeText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clock.Start();
        TimeText.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>
    /// Called by MainViewModel whenever CartLines or the invoice totals change.
    /// Thread-safe — dispatches to UI thread.
    /// </summary>
    public void Update(
        IReadOnlyList<CartLineDto> lines,
        decimal subtotal,
        decimal taxPercent,
        decimal taxAmount,
        decimal total,
        bool pricesIncludeVat,
        string? currencySuffix = null)
    {
        var suffix = string.IsNullOrWhiteSpace(currencySuffix) ? null : currencySuffix.Trim();
        Dispatcher.BeginInvoke(() =>
        {
            // Subtotal/VAT are only worth showing when VAT is added on top of the subtotal and
            // there's actually something to break down — otherwise a lone Total is clearer.
            var showBreakdown = lines.Count > 0 && !pricesIncludeVat;
            SubtotalRow.Visibility = showBreakdown ? Visibility.Visible : Visibility.Collapsed;
            VatRow.Visibility      = showBreakdown ? Visibility.Visible : Visibility.Collapsed;

            if (lines.Count == 0)
            {
                IdlePanel.Visibility  = Visibility.Visible;
                LinesList.Visibility  = Visibility.Collapsed;
                TotalText.Text = FormatAmount(0m, suffix);
            }
            else
            {
                IdlePanel.Visibility  = Visibility.Collapsed;
                LinesList.Visibility  = Visibility.Visible;
                LinesList.ItemsSource = lines.Select(ToDisplayLine).ToList();
                TotalText.Text = FormatAmount(total, suffix);

                if (showBreakdown)
                {
                    SubtotalText.Text = FormatAmount(subtotal, suffix);
                    CdVatLabelTb.Text = $"{Locale.Get("CustomerDisplay_VatPrefix")}" +
                        Locale.ToDisplayDigits(taxPercent.ToString("N0", CultureInfo.InvariantCulture)) + "%)";
                    VatText.Text = FormatAmount(taxAmount, suffix);
                }
            }
        });
    }

    private static DisplayLine ToDisplayLine(CartLineDto line)
    {
        // The customer only needs what they're actually paying per unit — fold the discount in
        // rather than showing the original price alongside a percentage to calculate from.
        var netUnitPrice = line.UnitPrice * (1 - line.DiscountPercent / 100m);
        var priceText = Locale.ToDisplayDigits(netUnitPrice.ToString("N2", CultureInfo.InvariantCulture));
        var qtyText   = Locale.ToDisplayDigits(line.Quantity.ToString("0.##", CultureInfo.InvariantCulture));
        var sumText   = Locale.ToDisplayDigits(line.LineTotal.ToString("N2", CultureInfo.InvariantCulture));

        return new DisplayLine(line.Name, priceText, qtyText, sumText);
    }

    private static string FormatAmount(decimal amount, string? suffix) =>
        Locale.ToDisplayDigits(amount.ToString("N2", CultureInfo.InvariantCulture))
        + (suffix is null ? "" : " " + suffix);

    private sealed record DisplayLine(string Name, string PriceText, string QtyText, string SumText);

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        base.OnClosed(e);
    }
}
