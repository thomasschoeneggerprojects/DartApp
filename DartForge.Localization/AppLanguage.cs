namespace DartForge.Localization;

/// <summary>
/// The user's language choice. Persisted by name (culture independent). <see cref="System"/> follows the OS UI language.
/// To add a language: add a value here, an entry in <see cref="SupportedLanguages.All"/> and a Resources.xx.resx file.
/// </summary>
public enum AppLanguage
{
    System,
    English,
    German,
    Spanish
}
