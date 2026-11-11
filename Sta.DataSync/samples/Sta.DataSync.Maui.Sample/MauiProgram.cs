using System.Reflection;
using Microsoft.Extensions.Logging;
using Sta.DataSync.Maui.Sample.Pages;
using Sta.DataSync.Maui.Sample.Services;

namespace Sta.DataSync.Maui.Sample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if DEBUG
        builder.Logging.AddDebug();
#endif

        // The app's own data. Synchronization never touches anything that is not registered below.
        var store = new SampleStore(FileSystem.AppDataDirectory);
        builder.Services.AddSingleton(store);

        // Sta.DataSync: engine + SQLite state store (separate from the app's database).
        builder.Services.AddDataSync(options =>
        {
            options.ApplicationId = "MauiSample";
            options.DefaultMode = SyncMode.Bidirectional;
            options.DefaultConflictStrategy = ConflictResolutionStrategy.KeepBoth;
        }, Path.Combine(FileSystem.AppDataDirectory, "sync", "sync-state.db3"));

        // Destination and sign-in. Android uses Google Identity Services (no client id in code);
        // Windows uses a "Desktop app" client whose id comes from oauth.props at build time.
        builder.Services.AddGoogleDriveSyncProvider(o => o.ApplicationName = "Sta.DataSync.MauiSample");
        builder.Services.AddGoogleDriveMauiAuthentication(o =>
        {
            o.DesktopClientId = BuildSetting("GoogleDesktopClientId");
            o.DesktopClientSecret = BuildSetting("GoogleDesktopClientSecret");
        });

        // What is synchronized: local JSON notes and a consistent snapshot of the records database.
        builder.Services.AddFileSyncSource("notes", store.NotesDirectory, o => o.Include.Add("*.json"));
        builder.Services.AddSqliteSnapshotSyncSource("records", store.DatabasePath, o =>
        {
            o.BeforeRestoreAsync = _ => store.CloseAsync();
            o.AfterRestoreAsync = _ => store.ReloadAsync();
        });

        // Automatic synchronization while the app is in the foreground.
        builder.Services.AddDataSyncScheduler(o =>
        {
            o.SyncOnStartup = true;
            o.Interval = TimeSpan.FromMinutes(15);
            o.ChangeDebounce = TimeSpan.FromSeconds(5);
        });
        builder.UseDataSync();

        builder.Services.AddSingleton<SyncSettingsViewModel>();
        builder.Services.AddSingleton<SyncSettingsPage>();
        return builder.Build();
    }

    private static string? BuildSetting(string key)
        => typeof(MauiProgram).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value ? value : null;
}
