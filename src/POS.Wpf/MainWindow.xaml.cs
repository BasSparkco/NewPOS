using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using POS.Wpf.ViewModels;

namespace POS.Wpf;

public partial class MainWindow : Window
{
    private const int ScannerBurstMaxGapMs  = 45;
    private const int ScannerResetGapMs     = 250;
    private const int ScannerMinLength      = 4;

    // Digits with at most one decimal point — shared by Qty, Discount and the calculator's own
    // Qty/Price boxes, all of which bind to plain numeric strings.
    private static readonly Regex NumericInputPattern = new(@"^\d*\.?\d*$", RegexOptions.Compiled);

    private readonly MainViewModel _vm;
    private readonly StringBuilder _scannerBuffer = new();
    private readonly List<int>     _scannerGapsMs = new();
    private DateTime               _lastScannerKeyAtUtc = DateTime.MinValue;

    // Snapshot of whatever text box was focused when the current burst attempt started, so a
    // confirmed scan can undo the one keystroke that always leaks through before the burst is
    // detectable (see Window_OnPreviewTextInput).
    private TextBox? _preScanFocusBox;
    private string?  _preScanFocusText;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Title = "SmartPOS";
        Loaded += async (_, _) => await vm.OnLoadedAsync();
    }

    // Attached to the Window (not just the search box) so a scanner works no matter which
    // control currently has focus — e.g. the cashier just edited a cart line's Qty/Disc box.
    // These are Preview (tunneling) handlers, so they see every keystroke on their way down to
    // whatever control is actually focused. Human-speed typing never satisfies IsScannerBurst, so
    // it always reaches that control untouched; genuine machine-speed input is blocked from the
    // control (and any single leaked keystroke undone) once recognized as a scan.
    private void Window_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
            return;

        var now = DateTime.UtcNow;
        var isBurstStart = _scannerBuffer.Length == 0;

        if (_lastScannerKeyAtUtc != DateTime.MinValue)
        {
            var gap = (int)(now - _lastScannerKeyAtUtc).TotalMilliseconds;
            if (gap > ScannerResetGapMs)
            {
                ResetScannerTracking();
                isBurstStart = true;
            }
            else
            {
                _scannerGapsMs.Add(gap);
            }
        }

        // The very first keystroke of a burst can't be classified yet (no gap to measure), so it
        // is allowed to reach whatever control is focused. Snapshot that control's text now —
        // before the keystroke is applied — so a confirmed scan can restore it on Enter instead of
        // leaving a stray scanned character sitting in a cart line's Qty/Disc box.
        if (isBurstStart)
        {
            _preScanFocusBox = e.OriginalSource as TextBox;
            _preScanFocusText = _preScanFocusBox?.Text;
        }

        _lastScannerKeyAtUtc = now;
        _scannerBuffer.Append(e.Text);

        // From the second keystroke on, a machine-speed gap is conclusive: stop letting further
        // characters leak into the focused control at all.
        if (_scannerGapsMs.Count > 0 && _scannerGapsMs.Max() <= ScannerBurstMaxGapMs)
            e.Handled = true;
    }

    private async void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        var candidate = _scannerBuffer.ToString().Trim();
        var looksLikeScannerBurst = IsScannerBurst(candidate);

        if (looksLikeScannerBurst && _preScanFocusBox is not null)
        {
            _preScanFocusBox.Text = _preScanFocusText ?? string.Empty;
            _preScanFocusBox.CaretIndex = _preScanFocusBox.Text.Length;
        }

        ResetScannerTracking();

        if (!looksLikeScannerBurst)
            return;

        e.Handled = true;
        await _vm.ProcessBarcodeScanAsync(candidate);
    }

    private void Window_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
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

    // Clicking/tabbing into the calculator's own Qty/Price box routes the on-screen keypad to it,
    // mirroring the cart line boxes above.
    private void CalcQtyBox_OnGotFocus(object sender, RoutedEventArgs e)
        => _vm.SelectCalcFieldCommand.Execute("Qty");

    private void CalcPriceBox_OnGotFocus(object sender, RoutedEventArgs e)
        => _vm.SelectCalcFieldCommand.Execute("Price");

    // Rejects anything but digits and a single decimal point, whether it's the scanner burst logic
    // above letting a keystroke through unclassified or genuine keyboard typing. Runs after the
    // Window-level scanner handlers in the tunnel, so it never even fires for a keystroke they've
    // already claimed (a confirmed scanner burst).
    private void NumericTextBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        var proposed = box.Text.Remove(box.SelectionStart, box.SelectionLength)
                                .Insert(box.SelectionStart, e.Text);
        e.Handled = !NumericInputPattern.IsMatch(proposed);
    }

    private void NumericTextBox_OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text) ||
            !NumericInputPattern.IsMatch((string)e.DataObject.GetData(DataFormats.Text)))
        {
            e.CancelCommand();
        }
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
        _preScanFocusBox = null;
        _preScanFocusText = null;
    }
}
