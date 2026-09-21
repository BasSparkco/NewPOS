using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace POS.Wpf.Converters;

/// <summary>
/// Returns Visible when the bound string is null/empty/whitespace (for placeholder display).
/// Set Inverted=true to get Visible when the string has content instead.
/// </summary>
public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public bool Inverted { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = string.IsNullOrWhiteSpace(value as string);
        var show = Inverted ? !isEmpty : isEmpty;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
