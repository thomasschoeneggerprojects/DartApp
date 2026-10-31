namespace DartForge.App.Services;

/// <summary>
/// Bridge between the Blazor UI and the native ad banner.
/// The UI says where a banner is welcome; the Android layer supplies the privacy-options action.
/// </summary>
public sealed class AdState
{
    /// <summary>False while a match is being played, so nobody taps an ad instead of a number.</summary>
    public bool BannerWanted { get; private set; } = true;
    public event Action? Changed;

    public void SetBannerWanted(bool wanted)
    {
        if (BannerWanted == wanted) return;
        BannerWanted = wanted;
        Changed?.Invoke();
    }

    /// <summary>Opens Google's consent form again (GDPR "privacy options"). Set by the Android activity.</summary>
    public Action? ShowPrivacyOptions { get; set; }
}
