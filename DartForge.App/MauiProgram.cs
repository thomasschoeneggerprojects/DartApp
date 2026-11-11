using DartForge.App.Services;
using DartForge.Localization;
using DartForge.Streaks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SportTA.Streaks;
using Sta.DataSync;
using Sta.DataSync.Maui;
using StreakCelebration.Hosting;
using StreakCelebration.Models;
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
        var store = new LocalStore();
        builder.Services.AddSingleton(store);
        AddCloudSync(builder, store);
        // Streak data (streaks-*.json) lives next to dartforge.db3, in the same directory.
        builder.Services.AddDartTaStreaks(LocalStore.DataDirectory);
        // Streak celebrations: reusable overlay (StreakCelebration.Maui) in DartTA's gold branding. The overlay itself sits
        // in MainPage.xaml; streak events reach it through the streak library's IStreakEventHandler.
        builder.UseStreakCelebrations(options =>
        {
            var brand = new CelebrationPalette
            {
                PrimaryColor = "#FF8A3D",
                SecondaryColor = "#F2C14E",
                CardColor = "#18232E",
                CountTextColor = "#F2C14E",
                TitleTextColor = "#FFFFFF",
                MessageTextColor = "#AEB9C5",
                BackdropColor = "#05090D",
            };
            // DartTA is always dark, so both OS themes use the same palette.
            options.LightPalette = brand;
            options.DarkPalette = brand;
            options.BackgroundOpacity = 0.6;
            options.DismissOnTap = true;
        });
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IStreakEventHandler, DartTaCelebrationHandler>());
        builder.Services.AddSingleton<GameSession>();
        builder.Services.AddSingleton<AdState>();
        builder.Services.AddSingleton<DartForge.Core.Engine.DartBot>();
        var app = builder.Build();
        // Apply the resolved UI culture before the first page renders.
        app.Services.GetRequiredService<UiCultureApplier>();
        return app;
    }

    /// <summary>
    /// Cloud backup and sync (Sta.DataSync): the match and training history goes to the user's Google Drive app data
    /// folder. Sign-in uses Google Identity Services, which needs an "Android" OAuth client for com.dartforge.app with the
    /// signing certificate SHA-1s in Google Cloud (no client id or secret in the app). Sync metadata lives in its own
    /// database next to dartforge.db3.
    /// </summary>
    private static void AddCloudSync(MauiAppBuilder builder, LocalStore store)
    {
        builder.Services.AddDataSync(options =>
        {
            options.ApplicationId = "DartTA";
            options.DefaultMode = SyncMode.Bidirectional;
            // A whole-database snapshot cannot hold two versions: the user decides (Settings) instead of anything being overwritten.
            options.DefaultConflictStrategy = ConflictResolutionStrategy.Manual;
        }, Path.Combine(LocalStore.DataDirectory, "sync", "sync-state.db3"));
        builder.Services.AddGoogleDriveSyncProvider(o => o.ApplicationName = "DartTA");
        builder.Services.AddGoogleDriveMauiAuthentication(_ => { });
        builder.Services.AddSqliteSnapshotSyncSource(CloudSync.HistoryScope, Path.Combine(LocalStore.DataDirectory, LocalStore.DatabaseFileName), o =>
        {
            o.BeforeRestoreAsync = store.BeginRestoreAsync;
            o.AfterRestoreAsync = _ => store.EndRestoreAsync();
        });
        builder.Services.AddDataSyncScheduler(o =>
        {
            o.SyncOnStartup = true;
            o.Interval = TimeSpan.FromMinutes(15);
            o.ChangeDebounce = TimeSpan.FromSeconds(10);
        });
        // The scheduler is started by CloudSync only while an account is connected and auto sync is on.
        builder.UseDataSync(o => o.StartSchedulerOnResume = false);
        builder.Services.AddSingleton<CloudSync>();
    }
}
