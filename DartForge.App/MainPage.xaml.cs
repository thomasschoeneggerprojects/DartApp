using DartForge.App.Services;
namespace DartForge.App;
public partial class MainPage : ContentPage
{
    private readonly AdState _ads;

    public MainPage(AdState ads)
    {
        InitializeComponent();
        _ads = ads;
        _ads.Changed += () => MainThread.BeginInvokeOnMainThread(UpdateBanner);
        UpdateBanner();
    }

    /// <summary>The banner is hidden during a live match so nobody taps an ad instead of a number.</summary>
    private void UpdateBanner() => Banner.IsVisible = _ads.BannerWanted;
}
