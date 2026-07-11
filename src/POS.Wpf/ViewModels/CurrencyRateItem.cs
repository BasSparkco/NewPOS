using CommunityToolkit.Mvvm.ComponentModel;

namespace POS.Wpf.ViewModels;

public partial class CurrencyRateItem : ObservableObject
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Symbol { get; init; }

    [ObservableProperty]
    private decimal _exchangeRate;

    public string DisplayLabel => string.IsNullOrWhiteSpace(Symbol)
        ? $"{Code} - {Name}"
        : $"{Code} ({Symbol}) - {Name}";
}