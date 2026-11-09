using Microsoft.Maui.ApplicationModel;
using System.Globalization;
using DartForge.Localization;
namespace DartForge.App.Services;

/// <summary>
/// Mirrors the localization service's culture into the .NET culture settings, so framework formatting (Blazor bindings,
/// default ToString calls) matches the chosen language. The localization service itself never relies on these.
/// </summary>
public sealed class UiCultureApplier : IDisposable
{
    private readonly ILocalizationService _loc;

    public UiCultureApplier(ILocalizationService loc)
    {
        _loc = loc;
        _loc.LanguageChanged += OnChanged;
        Apply();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (MainThread.IsMainThread) Apply(); else MainThread.BeginInvokeOnMainThread(Apply);
    }

    private void Apply()
    {
        var culture = _loc.CurrentCulture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public void Dispose() => _loc.LanguageChanged -= OnChanged;
}
