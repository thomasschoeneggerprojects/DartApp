namespace DartForge.App.Ads;

/// <summary>
/// AdMob identifiers of the DartTA AdMob account.
///   - AppId must match the APPLICATION_ID meta-data in Platforms/Android/AndroidManifest.xml.
///   - Debug builds use Google's public TEST banner unit so you never click your own live ads
///     while developing; Release builds use the real banner unit.
/// </summary>
public static class AdConfig
{
    /// <summary>AdMob app id (must match AndroidManifest.xml).</summary>
    public const string AppId = "ca-app-pub-3964073132044809~2715564069";

#if DEBUG
    /// <summary>Google test adaptive-banner unit id (Debug builds only).</summary>
    public const string BannerUnitId = "ca-app-pub-3940256099942544/9214589741";
#else
    /// <summary>DartTA adaptive-banner unit id.</summary>
    public const string BannerUnitId = "ca-app-pub-3964073132044809/4346220831";
#endif
}
