using System.Globalization;
using System.Windows.Data;
using POS.Wpf.Localization;

namespace POS.Wpf.Converters;

/// <summary>Converts a product's IsActive flag to the status pill text ("Active" / "Inactive").</summary>
public sealed class ActiveStatusLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Locale.Get("ProductMgmt_StatusActive") : Locale.Get("ProductMgmt_StatusInactive");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Converts a product's IsActive flag to the quick-action button text ("Deactivate" / "Activate").</summary>
public sealed class ActiveToggleLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Locale.Get("ProductMgmt_Deactivate") : Locale.Get("ProductMgmt_Activate");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
