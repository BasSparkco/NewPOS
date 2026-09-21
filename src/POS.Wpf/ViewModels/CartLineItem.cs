using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using POS.Application.Models;
using POS.Wpf.Localization;

namespace POS.Wpf.ViewModels;

/// <summary>
/// Editable, live-calculating wrapper around a <see cref="CartLineDto"/>. Typing into
/// <see cref="QtyText"/> or <see cref="DiscText"/> recalculates <see cref="LineTotal"/> instantly
/// and — after a short pause in typing — persists the change via the owning ViewModel's commit
/// callback, so the cart line no longer needs an explicit "apply" button.
/// </summary>
public sealed partial class CartLineItem : ObservableObject
{
    private const int DebounceMilliseconds = 450;

    private readonly DispatcherTimer _qtyTimer;
    private readonly DispatcherTimer _discTimer;
    private readonly Action<CartLineItem> _onQtyCommit;
    private readonly Action<CartLineItem> _onDiscCommit;

    public Guid LineId { get; }
    public Guid ProductId { get; }
    public string Name { get; }
    public decimal UnitPrice { get; }
    public string? ImagePath { get; }

    [ObservableProperty] private string _qtyText;
    [ObservableProperty] private string _discText;
    [ObservableProperty] private bool _isQtyCalcActive;
    [ObservableProperty] private bool _isDiscCalcActive;

    public CartLineItem(CartLineDto dto, Action<CartLineItem> onQtyCommit, Action<CartLineItem> onDiscCommit)
    {
        LineId      = dto.LineId;
        ProductId   = dto.ProductId;
        Name        = dto.Name;
        UnitPrice   = dto.UnitPrice;
        ImagePath   = dto.ImagePath;
        _qtyText    = FormatNumber(dto.Quantity);
        _discText   = FormatNumber(dto.DiscountPercent);
        _onQtyCommit  = onQtyCommit;
        _onDiscCommit = onDiscCommit;

        _qtyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
        _qtyTimer.Tick += (_, _) => { _qtyTimer.Stop(); _onQtyCommit(this); };

        _discTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
        _discTimer.Tick += (_, _) => { _discTimer.Stop(); _onDiscCommit(this); };
    }

    public decimal Quantity        => ParseOrZero(QtyText);
    public decimal DiscountPercent => Math.Clamp(ParseOrZero(DiscText), 0m, 100m);
    public decimal LineTotal       => Math.Round(Quantity * UnitPrice * (1 - DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero);

    public string FormattedUnitPrice => Locale.ToDisplayDigits(UnitPrice.ToString("N2", CultureInfo.InvariantCulture));
    public string FormattedLineTotal => Locale.ToDisplayDigits(LineTotal.ToString("N2", CultureInfo.InvariantCulture));

    /// <summary>Call after the global digit-display mode changes so already-rendered lines pick it up.</summary>
    public void NotifyDigitDisplayChanged()
    {
        OnPropertyChanged(nameof(FormattedUnitPrice));
        OnPropertyChanged(nameof(FormattedLineTotal));
    }

    partial void OnQtyTextChanged(string value)
    {
        OnPropertyChanged(nameof(Quantity));
        OnPropertyChanged(nameof(LineTotal));
        OnPropertyChanged(nameof(FormattedLineTotal));
        _qtyTimer.Stop();
        _qtyTimer.Start();
    }

    partial void OnDiscTextChanged(string value)
    {
        OnPropertyChanged(nameof(DiscountPercent));
        OnPropertyChanged(nameof(LineTotal));
        OnPropertyChanged(nameof(FormattedLineTotal));
        _discTimer.Stop();
        _discTimer.Start();
    }

    /// <summary>Stops pending debounce timers. Call before discarding this instance (e.g. on cart refresh).</summary>
    public void CancelPendingCommits()
    {
        _qtyTimer.Stop();
        _discTimer.Stop();
    }

    public CartLineDto ToDto() => new(LineId, ProductId, Name, Quantity, UnitPrice, DiscountPercent, LineTotal, ImagePath);

    private static decimal ParseOrZero(string text) =>
        decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m;

    private static string FormatNumber(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
