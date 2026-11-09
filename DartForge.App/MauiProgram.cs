using DartForge.App.Services;
using DartForge.Localization;
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
        // Localization: OS culture is captured first (before the app applies its own culture), then the saved preference is resolved.
        var systemCulture = new StartupSystemCultureProvider();
        builder.Services.AddSingleton<ISystemCultureProvider>(systemCulture);
        builder.Services.AddSingleton<ILanguagePreferenceStore, PreferencesLanguageStore>();
        builder.Services.AddSingleton<ILocalizationService, LocalizationService>();
        builder.Services.AddSingleton<UiCultureApplier>();
        builder.Services.AddSingleton<LocalStore>();
        builder.Services.AddSingleton<GameSession>();
        builder.Services.AddSingleton<AdState>();
        builder.Services.AddSingleton<DartForge.Core.Engine.DartBot>();
        var app = builder.Build();
        // Apply the resolved UI culture before the first page renders.
        app.Services.GetRequiredService<UiCultureApplier>();
        return app;
    }
}
