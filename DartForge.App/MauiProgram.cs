using DartForge.App.Services;
using Microsoft.Extensions.Logging;
namespace DartForge.App;
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>()
            .ConfigureMauiHandlers(handlers => handlers.AddHandler<DartForge.App.Controls.AdBanner, DartForge.App.Ads.AdBannerHandler>());
        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        builder.Services.AddSingleton<LocalStore>();
        builder.Services.AddSingleton<GameSession>();
        builder.Services.AddSingleton<AdState>();
        builder.Services.AddSingleton<DartForge.Core.Engine.DartBot>();
        return builder.Build();
    }
}
