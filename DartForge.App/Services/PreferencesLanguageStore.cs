using Microsoft.Maui.Storage;
using DartForge.Localization;
namespace DartForge.App.Services;

/// <summary>Stores the UI language in MAUI <see cref="Preferences"/> (Android SharedPreferences / Windows app settings) by enum name.</summary>
public sealed class PreferencesLanguageStore : ILanguagePreferenceStore
{
    internal const string Key = "dartta.language";

    public AppLanguage Load()
    {
        var stored = Preferences.Default.Get(Key, nameof(AppLanguage.System));
        return Enum.TryParse<AppLanguage>(stored, ignoreCase: true, out var language) && Enum.IsDefined(language) ? language : AppLanguage.System;
    }

    public void Save(AppLanguage language) => Preferences.Default.Set(Key, language.ToString());
}
