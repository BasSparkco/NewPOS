using System.Globalization;
using System.Windows.Data;
using POS.Wpf.Localization;

namespace POS.Wpf.Converters;

public sealed class BarcodeFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s))
            return "";
        return string.Format(CultureInfo.CurrentUICulture, Locale.Get("Barcode_Format"), s);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
