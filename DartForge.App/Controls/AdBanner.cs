namespace DartForge.App.Controls;

/// <summary>Native AdMob banner. Rendered by <c>AdBannerHandler</c> on Android; collapses to 0 height until an ad has loaded.</summary>
public sealed class AdBanner : View
{
    public static readonly BindableProperty AdUnitIdProperty =
        BindableProperty.Create(nameof(AdUnitId), typeof(string), typeof(AdBanner), null);

    /// <summary>Optional override; defaults to <c>AdConfig.BannerUnitId</c>.</summary>
    public string? AdUnitId
    {
        get => (string?)GetValue(AdUnitIdProperty);
        set => SetValue(AdUnitIdProperty, value);
    }
}
