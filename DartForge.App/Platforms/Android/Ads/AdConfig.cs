namespace DartForge.App.Ads;

/// <summary>
/// AdMob identifiers. These are Google's public TEST ids — they always serve test ads.
/// Before publishing, replace BOTH with the ids from your AdMob account:
///   1. BannerUnitId below
///   2. the APPLICATION_ID meta-data in Platforms/Android/AndroidManifest.xml
/// Never click your own live ads; keep the test ids while developing.
/// </summary>
public static class AdConfig
{
    /// <summary>Google test app id (must match AndroidManifest.xml).</summary>
    public const string AppId = "ca-app-pub-3940256099942544~3347511713";

    /// <summary>Google test adaptive-banner unit id.</summary>
    public const string BannerUnitId = "ca-app-pub-3940256099942544/9214589741";
}
