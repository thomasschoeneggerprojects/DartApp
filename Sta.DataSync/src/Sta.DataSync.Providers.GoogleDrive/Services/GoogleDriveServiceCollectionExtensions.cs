using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sta.DataSync;
using Sta.DataSync.Providers.GoogleDrive.Authentication;
using Sta.DataSync.Providers.GoogleDrive.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the Google Drive provider.</summary>
public static class GoogleDriveServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GoogleDriveSyncProvider"/> as an <see cref="ISyncProvider"/> (id <c>google-drive</c>).
    /// An <see cref="IGoogleAccessTokenProvider"/> must also be registered, e.g. by <see cref="AddGoogleDesktopAuthentication"/>
    /// or by the MAUI integration.
    /// </summary>
    public static IServiceCollection AddGoogleDriveSyncProvider(this IServiceCollection services, Action<GoogleDriveProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        OptionsBuilder<GoogleDriveProviderOptions> options = services.AddOptions<GoogleDriveProviderOptions>();
        if (configure is not null) options.Configure(configure);
        options.Validate(o => o.Validate().Count == 0, "Invalid Google Drive provider options.");

        services.TryAddSingleton<GoogleDriveSyncProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISyncProvider, GoogleDriveSyncProvider>(sp => sp.GetRequiredService<GoogleDriveSyncProvider>()));
        return services;
    }

    /// <summary>
    /// Registers OAuth for desktop applications (loopback redirect + PKCE) as <see cref="IGoogleAuthenticator"/> and
    /// <see cref="IGoogleAccessTokenProvider"/>. Register an <see cref="IGoogleTokenStore"/> too
    /// (e.g. <see cref="ProtectedFileTokenStore"/> on Windows).
    /// </summary>
    public static IServiceCollection AddGoogleDesktopAuthentication(this IServiceCollection services, Action<GoogleOAuthOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<GoogleOAuthOptions>().Configure(configure).Validate(o => o.Validate().Count == 0, "Invalid Google OAuth options.");
        services.TryAddSingleton<IOAuthBrowser>(_ => new LoopbackOAuthBrowser());
        services.TryAddSingleton<PkceGoogleAuthenticator>();
        services.TryAddSingleton<IGoogleAuthenticator>(sp => sp.GetRequiredService<PkceGoogleAuthenticator>());
        services.TryAddSingleton<IGoogleAccessTokenProvider>(sp => sp.GetRequiredService<PkceGoogleAuthenticator>());
        return services;
    }
}
