using System.Globalization;
using System.Resources;
using System.Windows;

namespace POS.Wpf.Localization;

/// <summary>
/// Loads strings from embedded <c>AppStrings.resx</c> / <c>AppStrings.*.resx</c> using <see cref="CultureInfo.CurrentUICulture"/>.
/// </summary>
public static class Locale
{
    private static readonly ResourceManager Rm = new(
        "POS.Wpf.Localization.AppStrings",
        typeof(Locale).Assembly);

    public static string Get(string name)
    {
        var s = Rm.GetString(name, CultureInfo.CurrentUICulture);
        if (!string.IsNullOrEmpty(s))
            return s;
        s = Rm.GetString(name, CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(s) ? name : s;
    }

    public static void ApplyFlowDirection(FrameworkElement root) =>
        root.FlowDirection = CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
}
