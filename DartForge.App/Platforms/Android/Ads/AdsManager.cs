using Android.App;
using Android.Gms.Ads;
using Xamarin.Google.UserMesssagingPlatform; // sic: the binding's namespace really has three "s"
namespace DartForge.App.Ads;

/// <summary>
/// Google UMP consent (required for users in the EEA/UK) followed by Mobile Ads SDK start-up.
/// Ads are only requested once <c>CanRequestAds()</c> is true.
/// </summary>
public static class AdsManager
{
    private static IConsentInformation? _consent;
    private static bool _initialized;

    public static bool CanShowAds { get; private set; }
    public static event Action? Ready;

    public static void Start(Activity activity)
    {
        try
        {
            _consent = UserMessagingPlatform.GetConsentInformation(activity);
            var parameters = new ConsentRequestParameters.Builder().SetTagForUnderAgeOfConsent(false).Build();
            _consent.RequestConsentInfoUpdate(activity, parameters,
                new UpdateSuccess(() => UserMessagingPlatform.LoadAndShowConsentFormIfRequired(activity, new Dismissed(_ => Init(activity)))),
                new UpdateFailure(error =>
                {
                    System.Diagnostics.Debug.WriteLine($"[Ads] consent update failed: {error?.Message}");
                    Init(activity);
                }));
            // Consent given in an earlier session lets ads start right away.
            Init(activity);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Ads] start failed: {ex}");
        }
    }

    /// <summary>Re-opens the consent form so the user can change or withdraw consent.</summary>
    public static void ShowPrivacyOptions(Activity activity)
    {
        try { UserMessagingPlatform.ShowPrivacyOptionsForm(activity, new Dismissed(_ => { })); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Ads] privacy options failed: {ex}"); }
    }

    private static void Init(Activity activity)
    {
        if (_initialized || _consent is null || !_consent.CanRequestAds()) return;
        _initialized = true;
        MobileAds.Initialize(activity);
        CanShowAds = true;
        Ready?.Invoke();
    }

    private sealed class UpdateSuccess(Action action) : Java.Lang.Object, IConsentInformationOnConsentInfoUpdateSuccessListener
    {
        public void OnConsentInfoUpdateSuccess() => action();
    }

    private sealed class UpdateFailure(Action<FormError?> action) : Java.Lang.Object, IConsentInformationOnConsentInfoUpdateFailureListener
    {
        public void OnConsentInfoUpdateFailure(FormError error) => action(error);
    }

    private sealed class Dismissed(Action<FormError?> action) : Java.Lang.Object, IConsentFormOnConsentFormDismissedListener
    {
        public void OnConsentFormDismissed(FormError? error) => action(error);
    }
}
