using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace POS.Wpf.Converters;

/// <summary>
/// Returns Visible when the bound value is non-null. Set Inverted=true to get Visible when null.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Inverted { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null;
        var show = Inverted ? isNull : !isNull;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
