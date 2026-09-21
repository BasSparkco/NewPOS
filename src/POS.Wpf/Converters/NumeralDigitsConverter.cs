using System.Globalization;
using System.Windows.Data;
using POS.Wpf.Localization;

namespace POS.Wpf.Converters;

/// <summary>
/// Formats a numeric value with a fixed Western-digit culture (mirroring <c>Binding.StringFormat</c>
/// semantics) and then applies <see cref="Locale.ToDisplayDigits"/>, so the result is always plain
/// 0-9 unless the Arabic "Indian numerals" setting is on. Use in place of <c>Binding.StringFormat</c>
/// for any bound number shown to the user: pass the same format via <c>ConverterParameter</c>, e.g.
/// <c>Converter={StaticResource Digits}, ConverterParameter=N2</c> or a composite pattern like
/// <c>ConverterParameter='×{0:N0}'</c>.
/// </summary>
public sealed class NumeralDigitsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return string.Empty;

        var format = parameter as string;
        string text;
        if (string.IsNullOrEmpty(format))
        {
            text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? string.Empty;
        }
        else if (format.Contains("{0"))
        {
            text = string.Format(CultureInfo.InvariantCulture, format, value);
        }
        else
        {
            text = value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value.ToString() ?? string.Empty;
        }

        return Locale.ToDisplayDigits(text);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
