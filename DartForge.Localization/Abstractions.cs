using System.Globalization;
namespace DartForge.Localization;

/// <summary>Central access to translated UI text and the active UI culture.</summary>
public interface ILocalizationService
{
    /// <summary>Culture used for resource lookups and for formatting numbers, percentages and dates.</summary>
    CultureInfo CurrentCulture { get; }

    /// <summary>The stored preference (may be <see cref="AppLanguage.System"/>).</summary>
    AppLanguage SelectedLanguage { get; }

    /// <summary>The translated language actually shown (never System).</summary>
    LanguageInfo ActiveLanguage { get; }

    /// <summary>Text for a key: selected language → English → the key itself. Never throws.</summary>
    string GetString(string resourceKey);

    /// <summary>Formatted text with the current culture. Invalid templates fall back safely. Never throws.</summary>
    string GetString(string resourceKey, params object[] arguments);

    /// <summary>Switches the UI language at runtime, persists the choice and raises <see cref="LanguageChanged"/>.</summary>
    void SetLanguage(AppLanguage language);

    /// <summary>Raised after the language changed (on the calling thread). UI handlers must dispatch to their UI thread.</summary>
    event EventHandler? LanguageChanged;
}

/// <summary>Persists the language preference (MAUI Preferences in the app, in-memory in tests).</summary>
public interface ILanguagePreferenceStore
{
    AppLanguage Load();
    void Save(AppLanguage language);
}

/// <summary>Supplies the operating system's UI culture.</summary>
public interface ISystemCultureProvider
{
    CultureInfo SystemUICulture { get; }
}

/// <summary>
/// Captures <see cref="CultureInfo.CurrentUICulture"/> when created – i.e. the OS language at app start, before the app
/// applies its own culture. Register it as a singleton and create it before the localization service.
/// </summary>
public sealed class StartupSystemCultureProvider : ISystemCultureProvider
{
    public CultureInfo SystemUICulture { get; } = CultureInfo.CurrentUICulture;
}

/// <summary>Non-persistent store (tests, previews).</summary>
public sealed class InMemoryLanguagePreferenceStore(AppLanguage initial = AppLanguage.System) : ILanguagePreferenceStore
{
    private volatile int _value = (int)initial;
    public AppLanguage Load() => (AppLanguage)_value;
    public void Save(AppLanguage language) => _value = (int)language;
}
