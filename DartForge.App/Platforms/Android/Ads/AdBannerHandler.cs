using Android.Gms.Ads;
using Android.Views;
using Android.Widget;
using DartForge.App.Controls;
using Microsoft.Maui.Handlers;
namespace DartForge.App.Ads;

/// <summary>Hosts an anchored adaptive AdMob banner inside a FrameLayout.</summary>
public sealed class AdBannerHandler : ViewHandler<AdBanner, FrameLayout>
{
    public static readonly IPropertyMapper<AdBanner, AdBannerHandler> Mapper = new PropertyMapper<AdBanner, AdBannerHandler>(ViewMapper);
    private AdView? _adView;

    public AdBannerHandler() : base(Mapper) { }

    protected override FrameLayout CreatePlatformView() => new(Context);

    protected override void ConnectHandler(FrameLayout platformView)
    {
        base.ConnectHandler(platformView);
        AdsManager.Ready += OnAdsReady;
        if (AdsManager.CanShowAds) OnAdsReady();
    }

    protected override void DisconnectHandler(FrameLayout platformView)
    {
        AdsManager.Ready -= OnAdsReady;
        platformView.RemoveAllViews();
        _adView?.Destroy();
        _adView = null;
        base.DisconnectHandler(platformView);
    }

    private void OnAdsReady() => MainThread.BeginInvokeOnMainThread(LoadBanner);

    private void LoadBanner()
    {
        if (_adView is not null || PlatformView is null || VirtualView is null) return;
        var metrics = Context.Resources!.DisplayMetrics!;
        var widthDp = (int)(metrics.WidthPixels / metrics.Density);
        var size = AdSize.GetCurrentOrientationAnchoredAdaptiveBannerAdSize(Context, widthDp);
        _adView = new AdView(Context) { AdUnitId = VirtualView.AdUnitId ?? AdConfig.BannerUnitId, AdSize = size };
        _adView.AdListener = new BannerListener(this, size.Height);
        PlatformView.AddView(_adView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.CenterHorizontal));
        _adView.LoadAd(new AdRequest.Builder().Build());
    }

    private sealed class BannerListener(AdBannerHandler handler, int heightDp) : AdListener
    {
        public override void OnAdLoaded()
        {
            base.OnAdLoaded();
            if (handler.VirtualView is { } banner) banner.HeightRequest = heightDp;
        }

        public override void OnAdFailedToLoad(LoadAdError error)
        {
            base.OnAdFailedToLoad(error);
            System.Diagnostics.Debug.WriteLine($"[Ads] banner failed: {error.Message}");
            if (handler.VirtualView is { } banner) banner.HeightRequest = 0;
        }
    }
}
