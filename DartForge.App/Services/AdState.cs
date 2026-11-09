namespace DartForge.App.Services;

/// <summary>
/// Bridge that lets the Blazor UI open the native Google privacy-options form.
/// </summary>
public sealed class AdState
{
    /// <summary>Opens Google's consent form again (GDPR "privacy options"). Set by the Android activity.</summary>
    public Action? ShowPrivacyOptions { get; set; }
}
