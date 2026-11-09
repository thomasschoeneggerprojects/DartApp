using DartForge.Localization;
using Microsoft.AspNetCore.Components;
namespace DartForge.App.Components;

/// <summary>
/// Base for components that show translated text. Re-renders itself when the language changes, so every visible text
/// updates immediately (also during a running match) without touching component or game state.
/// </summary>
public abstract class LocalizedComponentBase : ComponentBase, IDisposable
{
    [Inject] protected ILocalizationService Loc { get; set; } = default!;
    private bool _subscribed;

    public override Task SetParametersAsync(ParameterView parameters)
    {
        if (!_subscribed) { Loc.LanguageChanged += OnLanguageChanged; _subscribed = true; }
        return base.SetParametersAsync(parameters);
    }

    /// <summary>Translated text for a resource key.</summary>
    protected string L(string key) => Loc.GetString(key);

    /// <summary>Translated, culture-formatted text with arguments.</summary>
    protected string L(string key, params object[] args) => Loc.GetString(key, args);

    /// <summary>Called on the renderer's thread after a language switch, before re-rendering.</summary>
    protected virtual void OnLanguageChangedCore() { }

    private void OnLanguageChanged(object? sender, EventArgs e) => _ = InvokeAsync(() =>
    {
        OnLanguageChangedCore();
        StateHasChanged();
    });

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && _subscribed) { Loc.LanguageChanged -= OnLanguageChanged; _subscribed = false; }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
