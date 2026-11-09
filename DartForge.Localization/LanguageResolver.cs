using System.Globalization;
namespace DartForge.Localization;

/// <summary>Pure function that turns a language preference and the OS culture into the culture the UI uses.</summary>
public static class LanguageResolver
{
    /// <summary>The translated language for a preference: manual choice wins, otherwise the OS language, otherwise English.</summary>
    public static LanguageInfo ResolveLanguage(AppLanguage preference, CultureInfo systemCulture)
        => (preference == AppLanguage.System ? null : SupportedLanguages.Find(preference))
           ?? SupportedLanguages.FindByCulture(systemCulture)
           ?? SupportedLanguages.English;

    /// <summary>
    /// The culture used for resources and number/date formatting. When the OS uses a regional variant of the chosen
    /// language (de-AT, es-MX, ...) that regional culture is kept so formatting matches the user's region.
    /// </summary>
    public static CultureInfo ResolveCulture(AppLanguage preference, CultureInfo systemCulture)
    {
        var language = ResolveLanguage(preference, systemCulture);
        if (!systemCulture.IsNeutralCulture && !Equals(systemCulture, CultureInfo.InvariantCulture) &&
            string.Equals(systemCulture.TwoLetterISOLanguageName, language.Code, StringComparison.OrdinalIgnoreCase))
            return CultureInfo.GetCultureInfo(systemCulture.Name);
        return language.Culture;
    }
}
