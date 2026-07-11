using System.Text;
using System.Windows;
using System.Windows.Input;
using POS.Wpf.ViewModels;

namespace POS.Wpf;

public partial class MainWindow : Window
{
    private const int ScannerBurstMaxGapMs  = 45;
    private const int ScannerResetGapMs     = 250;
    private const int ScannerMinLength      = 4;

    private readonly MainViewModel _vm;
    private readonly StringBuilder _scannerBuffer = new();
    private readonly List<int>     _scannerGapsMs = new();
    private DateTime               _lastScannerKeyAtUtc = DateTime.MinValue;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Title = "SmartPOS";
        Loaded += async (_, _) => await vm.OnLoadedAsync();
    }

    private void SearchBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
            return;

        var now = DateTime.UtcNow;
        if (_lastScannerKeyAtUtc != DateTime.MinValue)
        {
            var gap = (int)(now - _lastScannerKeyAtUtc).TotalMilliseconds;
            if (gap > ScannerResetGapMs)
            {
                ResetScannerTracking();
            }
            else
            {
                _scannerGapsMs.Add(gap);
            }
        }

        _lastScannerKeyAtUtc = now;
        _scannerBuffer.Append(e.Text);
    }

    private async void SearchBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        var candidate = _scannerBuffer.ToString().Trim();
        var looksLikeScannerBurst = IsScannerBurst(candidate);
        ResetScannerTracking();

        if (!looksLikeScannerBurst)
            return;

        e.Handled = true;
        await _vm.ProcessBarcodeScanAsync(candidate);
    }

    private void SearchBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => ResetScannerTracking();

    // Clicking/tabbing into a cart line's Qty or Discount box also routes the on-screen
    // calculator to that field, so the cashier can use either the keyboard or the keypad.
    private void CartLineQtyBox_OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CartLineItem line })
            _vm.SelectLineQtyFieldCommand.Execute(line);
    }

    private void CartLineDiscBox_OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CartLineItem line })
            _vm.SelectLineDiscFieldCommand.Execute(line);
    }

    private bool IsScannerBurst(string code)
    {
        if (code.Length < ScannerMinLength || _scannerGapsMs.Count == 0)
            return false;

        var maxGap = _scannerGapsMs.Max();
        return maxGap <= ScannerBurstMaxGapMs;
    }

    private void ResetScannerTracking()
    {
        _scannerBuffer.Clear();
        _scannerGapsMs.Clear();
        _lastScannerKeyAtUtc = DateTime.MinValue;
    }
}
