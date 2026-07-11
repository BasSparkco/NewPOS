using System.Collections.ObjectModel;
using System.Globalization;
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
        _clock.Tick += (_, _) => TimeText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clock.Start();
        TimeText.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>
    /// Called by MainViewModel whenever CartLines or Total changes.
    /// Thread-safe — dispatches to UI thread.
    /// </summary>
    public void Update(IReadOnlyList<CartLineDto> lines, decimal total, string? currencySuffix = null)
    {
        var suffix = string.IsNullOrWhiteSpace(currencySuffix) ? null : currencySuffix.Trim();
        Dispatcher.BeginInvoke(() =>
        {
            if (lines.Count == 0)
            {
                IdlePanel.Visibility  = Visibility.Visible;
                LinesList.Visibility  = Visibility.Collapsed;
                TotalText.Text = FormatTotal(0m, suffix);
            }
            else
            {
                IdlePanel.Visibility  = Visibility.Collapsed;
                LinesList.Visibility  = Visibility.Visible;
                LinesList.ItemsSource = new ObservableCollection<CartLineDto>(lines);
                TotalText.Text = FormatTotal(total, suffix);
            }
        });
    }

    private static string FormatTotal(decimal total, string? suffix) =>
        total.ToString("N2", CultureInfo.CurrentCulture)
        + (suffix is null ? "" : " " + suffix);

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        base.OnClosed(e);
    }
}
