using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Maui.LifecycleEvents;
using Sta.DataSync.Hosting;
using Sta.DataSync.Maui.Authentication;
using Sta.DataSync.Maui.Connectivity;
using Sta.DataSync.Providers.GoogleDrive.Authentication;

namespace Sta.DataSync.Maui;

/// <summary>Options of the MAUI integration.</summary>
public sealed class DataSyncMauiOptions
{
    /// <summary>Start the scheduler when the app comes to the foreground the first time.</summary>
    public bool StartSchedulerOnResume { get; set; } = true;

    /// <summary>Pause automatic synchronization while the app is in the background (Android, iOS). No background service is assumed.</summary>
    public bool PauseInBackground { get; set; } = true;
}

/// <summary>MAUI registration of Sta.DataSync platform services.</summary>
public static class DataSyncMauiExtensions
{
    /// <summary>
    /// Registers connectivity monitoring, secure token storage, main-thread events and lifecycle handling
    /// (scheduler starts and resumes in the foreground, pauses in the background). Call after <c>AddDataSync</c>
    /// and <c>AddDataSyncScheduler</c>.
    /// </summary>
    public static MauiAppBuilder UseDataSync(this MauiAppBuilder builder, Action<DataSyncMauiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new DataSyncMauiOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<IConnectivityMonitor, MauiConnectivityMonitor>();
        builder.Services.TryAddSingleton<IGoogleTokenStore, MauiSecureTokenStore>();
        builder.Services.TryAddSingleton<MainThreadSyncEvents>();
        builder.ConfigureLifecycleEvents(events => ConfigureLifecycle(events, options));
        return builder;
    }

    /// <summary>
    /// Registers Google sign-in for the current platform as <see cref="IGoogleAuthenticator"/> and
    /// <see cref="IGoogleAccessTokenProvider"/>: Google Identity Services on Android, loopback + PKCE on Windows,
    /// <see cref="WebAuthenticator"/> + PKCE on iOS (untested).
    /// </summary>
    public static IServiceCollection AddGoogleDriveMauiAuthentication(this IServiceCollection services, Action<MauiGoogleAuthOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new MauiGoogleAuthOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IGoogleTokenStore, MauiSecureTokenStore>();
#if ANDROID
        services.TryAddSingleton<AndroidGoogleAuthenticator>();
        services.TryAddSingleton<IGoogleAuthenticator>(sp => sp.GetRequiredService<AndroidGoogleAuthenticator>());
#else
        GoogleOAuthOptions oauth = ToOAuthOptions(options);
        if (oauth.Validate().Count > 0)
        {
            // Offline-first: a missing OAuth configuration must not stop the app; connecting reports it instead.
            services.TryAddSingleton<IGoogleAuthenticator>(new UnconfiguredGoogleAuthenticator(string.Join(" ", oauth.Validate())));
        }
        else
        {
            services.TryAddSingleton<IOAuthBrowser>(_ => CreateBrowser(options));
            services.TryAddSingleton<PkceGoogleAuthenticator>(sp => new PkceGoogleAuthenticator(
                Microsoft.Extensions.Options.Options.Create(oauth),
                sp.GetRequiredService<IOAuthBrowser>(),
                sp.GetRequiredService<IGoogleTokenStore>()));
            services.TryAddSingleton<IGoogleAuthenticator>(sp => sp.GetRequiredService<PkceGoogleAuthenticator>());
        }
#endif
        services.TryAddSingleton<IGoogleAccessTokenProvider>(sp => sp.GetRequiredService<IGoogleAuthenticator>());
        return services;
    }

#if !ANDROID
    private static IOAuthBrowser CreateBrowser(MauiGoogleAuthOptions options)
    {
#if IOS || MACCATALYST
        return new WebAuthenticatorOAuthBrowser(options.IosRedirectUri ?? throw new SyncConfigurationException("IosRedirectUri is required on iOS."));
#else
        return new LoopbackOAuthBrowser();
#endif
    }

    private static GoogleOAuthOptions ToOAuthOptions(MauiGoogleAuthOptions options)
    {
#if IOS || MACCATALYST
        var result = new GoogleOAuthOptions { ClientId = options.IosClientId ?? string.Empty };
#else
        var result = new GoogleOAuthOptions { ClientId = options.DesktopClientId ?? string.Empty, ClientSecret = options.DesktopClientSecret };
#endif
        result.Scopes.Clear();
        foreach (string scope in options.Scopes) result.Scopes.Add(scope);
        return result;
    }
#endif

    private static void ConfigureLifecycle(ILifecycleBuilder events, DataSyncMauiOptions options)
    {
#if ANDROID
        events.AddAndroid(android => android
            .OnResume(_ => Foreground(options))
            .OnStop(_ => Background(options))
            .OnActivityResult((activity, requestCode, resultCode, data) =>
                Services?.GetService<AndroidGoogleAuthenticator>()?.HandleActivityResult(activity, requestCode, resultCode, data)));
#elif IOS || MACCATALYST
        events.AddiOS(ios => ios
            .OnActivated(_ => Foreground(options))
            .DidEnterBackground(_ => Background(options)));
#elif WINDOWS
        events.AddWindows(windows => windows.OnActivated((_, _) => Foreground(options)));
#endif
    }

    private static IServiceProvider? Services => IPlatformApplication.Current?.Services;

    private static void Foreground(DataSyncMauiOptions options)
    {
        if (Services?.GetService<ISyncScheduler>() is not { } scheduler) return;
        if (!scheduler.IsRunning && options.StartSchedulerOnResume) _ = scheduler.StartAsync();
        if (scheduler.IsPaused && options.PauseInBackground) scheduler.ResumeAutomaticSync();
    }

    private static void Background(DataSyncMauiOptions options)
    {
        if (options.PauseInBackground && Services?.GetService<ISyncScheduler>() is { IsRunning: true } scheduler) scheduler.PauseAutomaticSync();
    }
}
