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

    /// <summary>
    /// When true (Arabic UI with the "Indian numerals" setting enabled), <see cref="ToDisplayDigits"/>
    /// rewrites Western digits (0-9) to Eastern Arabic-Indic digits (٠-٩) for on-screen display.
    /// Every other language/setting combination always shows plain Western digits, matching the English UI.
    /// </summary>
    public static bool UseEasternArabicDigits { get; set; }

    private static readonly char[] EasternArabicDigits = "٠١٢٣٤٥٦٧٨٩".ToCharArray();

    /// <summary>Rewrites the Western digits (0-9) in <paramref name="value"/> per <see cref="UseEasternArabicDigits"/>.</summary>
    public static string ToDisplayDigits(string? value)
    {
        if (!UseEasternArabicDigits || string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '0' and <= '9')
                chars[i] = EasternArabicDigits[chars[i] - '0'];
        }
        return new string(chars);
    }
}
