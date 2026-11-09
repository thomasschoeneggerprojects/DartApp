using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Resources;
namespace DartForge.Localization;

/// <summary>
/// <see cref="ILocalizationService"/> backed by the .resx files in <c>Localization/</c> via <see cref="ResourceManager"/>.
/// Thread safe: the active culture is held in one immutable snapshot that is swapped atomically, and lookups always pass
/// that culture explicitly (no dependency on thread-local culture in async code).
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    /// <summary>Manifest base name of Localization/Resources.resx.</summary>
    public const string ResourceBaseName = "DartForge.Localization.Localization.Resources";

    private static readonly ResourceManager SharedResources = new(ResourceBaseName, typeof(LocalizationService).Assembly);

    private sealed record Snapshot(AppLanguage Selected, LanguageInfo Active, CultureInfo Culture);

    private readonly ResourceManager _resources;
    private readonly ILanguagePreferenceStore _store;
    private readonly ISystemCultureProvider _system;
    private readonly Action<string> _reportMissing;
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private volatile Snapshot _state;

    public LocalizationService(ILanguagePreferenceStore store, ISystemCultureProvider system)
        : this(store, system, null, null) { }

    /// <param name="resources">Optional resource manager (tests). Defaults to the DartTA resources.</param>
    /// <param name="reportMissing">Receives each missing key or broken template once. Defaults to Debug output (debug builds only).</param>
    internal LocalizationService(ILanguagePreferenceStore store, ISystemCultureProvider system, ResourceManager? resources, Action<string>? reportMissing)
    {
        _store = store;
        _system = system;
        _resources = resources ?? SharedResources;
        _reportMissing = reportMissing ?? DefaultReport;
        AppLanguage saved;
        try { saved = store.Load(); } catch { saved = AppLanguage.System; }
        if (!Enum.IsDefined(saved)) saved = AppLanguage.System;
        _state = Build(saved);
    }

    public CultureInfo CurrentCulture => _state.Culture;
    public AppLanguage SelectedLanguage => _state.Selected;
    public LanguageInfo ActiveLanguage => _state.Active;

    public event EventHandler? LanguageChanged;

    public string GetString(string resourceKey)
    {
        if (string.IsNullOrEmpty(resourceKey)) return string.Empty;
        var culture = _state.Culture;
        string? value = null;
        try
        {
            // ResourceManager walks de-AT → de → neutral (English) by itself.
            value = _resources.GetString(resourceKey, culture);
            if (string.IsNullOrEmpty(value)) value = _resources.GetString(resourceKey, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is MissingManifestResourceException or MissingSatelliteAssemblyException or InvalidOperationException)
        {
            value = null;
        }
        if (!string.IsNullOrEmpty(value)) return value;
        ReportOnce(resourceKey, $"[DartTA localization] Missing resource key '{resourceKey}' ({culture.Name}).");
        return resourceKey;
    }

    public string GetString(string resourceKey, params object[] arguments)
    {
        var template = GetString(resourceKey);
        if (arguments is null || arguments.Length == 0) return template;
        try
        {
            return string.Format(_state.Culture, template, arguments);
        }
        catch (FormatException)
        {
            ReportOnce("fmt:" + resourceKey, $"[DartTA localization] Invalid format string for '{resourceKey}' ({_state.Culture.Name}).");
            // Try the English template, then give up gracefully (never interrupt a match).
            try { return string.Format(_state.Culture, _resources.GetString(resourceKey, CultureInfo.InvariantCulture) ?? resourceKey, arguments); }
            catch (FormatException) { return template; }
        }
    }

    public void SetLanguage(AppLanguage language)
    {
        if (!Enum.IsDefined(language)) language = AppLanguage.System;
        bool changed;
        lock (_gate)
        {
            var next = Build(language);
            changed = !Equals(next.Culture, _state.Culture) || next.Selected != _state.Selected;
            _state = next;
            try { _store.Save(language); }
            catch (Exception ex) { _reportMissing($"[DartTA localization] Could not save language preference: {ex.Message}"); }
        }
        if (changed) LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private Snapshot Build(AppLanguage selected)
    {
        var system = _system.SystemUICulture ?? CultureInfo.InvariantCulture;
        return new Snapshot(selected, LanguageResolver.ResolveLanguage(selected, system), LanguageResolver.ResolveCulture(selected, system));
    }

    private static void DefaultReport(string message) => Debug.WriteLine(message);

    private void ReportOnce(string id, string message)
    {
        if (_reported.TryAdd(id, 0)) _reportMissing(message);
    }
}
