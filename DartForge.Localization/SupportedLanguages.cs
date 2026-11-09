using System.Globalization;
namespace DartForge.Localization;

/// <summary>A translated UI language: enum value, ISO 639-1 code and its name in that language.</summary>
public sealed record LanguageInfo(AppLanguage Language, string Code, string NativeName)
{
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Code);
}

/// <summary>Registry of the UI languages DartTA ships. English is the default and fallback language.</summary>
public static class SupportedLanguages
{
    public static readonly LanguageInfo English = new(AppLanguage.English, "en", "English");

    /// <summary>All translated languages (excluding <see cref="AppLanguage.System"/>), in display order.</summary>
    public static IReadOnlyList<LanguageInfo> All { get; } =
    [
        English,
        new(AppLanguage.German, "de", "Deutsch"),
        new(AppLanguage.Spanish, "es", "Español"),
    ];

    public static LanguageInfo? Find(AppLanguage language) => All.FirstOrDefault(l => l.Language == language);

    /// <summary>Finds a translated language by its primary language code ("de" for de-AT, de-CH, ...).</summary>
    public static LanguageInfo? FindByCulture(CultureInfo? culture)
    {
        for (var c = culture; c is not null && !Equals(c, CultureInfo.InvariantCulture); c = c.Parent)
        {
            var code = c.TwoLetterISOLanguageName;
            var match = All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }
}
