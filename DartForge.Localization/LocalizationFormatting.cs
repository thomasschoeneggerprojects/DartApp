using System.Globalization;
namespace DartForge.Localization;

/// <summary>Culture-aware display formatting. Only affects display strings – never calculations or stored data.</summary>
public static class LocalizationFormatting
{
    /// <summary>A number with a fixed count of decimals (e.g. averages: 45.75 / 45,75).</summary>
    public static string FormatNumber(this ILocalizationService loc, double value, int decimals = 1)
        => value.ToString("F" + Math.Clamp(decimals, 0, 6), loc.CurrentCulture);

    /// <summary>An integer, culture formatted without group separators (scores stay "1001", not "1.001").</summary>
    public static string FormatInteger(this ILocalizationService loc, int value) => value.ToString("D", loc.CurrentCulture);

    /// <summary>A percentage given on a 0–100 scale: 62.5 → "62.5%" (en), "62,5 %" (de, es).</summary>
    public static string FormatPercent(this ILocalizationService loc, double percent, int decimals = 0)
    {
        var nf = (NumberFormatInfo)loc.CurrentCulture.NumberFormat.Clone();
        nf.PercentDecimalDigits = Math.Clamp(decimals, 0, 6);
        return (percent / 100.0).ToString("P", nf);
    }

    /// <summary>Short date in the UI culture.</summary>
    public static string FormatDate(this ILocalizationService loc, DateTimeOffset value) => value.ToString("d", loc.CurrentCulture);

    /// <summary>Short time in the UI culture.</summary>
    public static string FormatTime(this ILocalizationService loc, DateTimeOffset value) => value.ToString("t", loc.CurrentCulture);

    /// <summary>A match duration such as "1:05:09" or "12:30".</summary>
    public static string FormatDuration(this ILocalizationService loc, TimeSpan value)
        => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss", loc.CurrentCulture) : value.ToString(@"m\:ss", loc.CurrentCulture);
}
