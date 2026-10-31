using Android.App;
using Android.Content.PM;
using Android.OS;
using DartForge.App.Ads;
using DartForge.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
namespace DartForge.App;
[Activity(Theme="@style/Maui.SplashTheme", MainLauncher=true, ConfigurationChanges=ConfigChanges.ScreenSize|ConfigChanges.Orientation|ConfigChanges.UiMode|ConfigChanges.ScreenLayout|ConfigChanges.SmallestScreenSize|ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var adState = IPlatformApplication.Current?.Services.GetService<AdState>();
        if (adState is not null) adState.ShowPrivacyOptions = () => AdsManager.ShowPrivacyOptions(this);
        // Asks for GDPR consent where required, then starts the Mobile Ads SDK.
        AdsManager.Start(this);
    }
}
