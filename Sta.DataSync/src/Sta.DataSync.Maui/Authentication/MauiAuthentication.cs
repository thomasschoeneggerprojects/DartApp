using System.Text.Json;
using Sta.DataSync.Providers.GoogleDrive.Authentication;

namespace Sta.DataSync.Maui.Authentication;

/// <summary>Google sign-in settings for MAUI apps. Values come from configuration, never from source code.</summary>
public sealed class MauiGoogleAuthOptions
{
    /// <summary>Windows: client id of a "Desktop app" OAuth client (loopback redirect).</summary>
    public string? DesktopClientId { get; set; }

    /// <summary>Windows: the desktop client's secret (required by Google for the exchange; not confidential in installed apps).</summary>
    public string? DesktopClientSecret { get; set; }

    /// <summary>iOS: client id of an "iOS" OAuth client.</summary>
    public string? IosClientId { get; set; }

    /// <summary>iOS: redirect URI (reversed client id scheme), e.g. <c>com.googleusercontent.apps.123:/oauth2redirect</c>.</summary>
    public Uri? IosRedirectUri { get; set; }

    /// <summary>Requested scopes; least privilege by default.</summary>
    public IList<string> Scopes { get; } = [GoogleOAuthOptions.AppDataScope];
}

/// <summary>Token storage in MAUI <see cref="SecureStorage"/> (Android Keystore, iOS Keychain, Windows data protection).</summary>
public sealed class MauiSecureTokenStore : IGoogleTokenStore
{
    private const string Key = "sta.datasync.google.tokens";

    /// <inheritdoc />
    public async Task<GoogleTokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        string? json = await SecureStorage.Default.GetAsync(Key).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<GoogleTokenSet>(json);
        }
        catch (JsonException)
        {
            SecureStorage.Default.Remove(Key);
            return null;
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(GoogleTokenSet tokens, CancellationToken cancellationToken = default)
        => SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(tokens));

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        SecureStorage.Default.Remove(Key);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Authorization through MAUI <see cref="WebAuthenticator"/> (system browser session with custom-scheme redirect).
/// Intended for iOS with an "iOS" OAuth client. Not tested on a device in version 1.
/// </summary>
public sealed class WebAuthenticatorOAuthBrowser(Uri redirectUri) : IOAuthBrowser
{
    /// <inheritdoc />
    public async Task<OAuthBrowserResult> AuthorizeAsync(Func<Uri, Uri> buildAuthorizationUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildAuthorizationUrl);
        WebAuthenticatorResult result;
        try
        {
            result = await MainThread.InvokeOnMainThreadAsync(() => WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = buildAuthorizationUrl(redirectUri),
                CallbackUrl = redirectUri,
                PrefersEphemeralWebBrowserSession = true,
            })).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex)
        {
            throw new OperationCanceledException("Google sign-in was canceled.", ex, cancellationToken);
        }

        string query = string.Join('&', result.Properties.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return new OAuthBrowserResult(redirectUri, new Uri(redirectUri.GetLeftPart(UriPartial.Path) + "?" + query));
    }
}

/// <summary>Placeholder when no OAuth client is configured: the app keeps working offline and connecting explains why it fails.</summary>
internal sealed class UnconfiguredGoogleAuthenticator(string reason) : IGoogleAuthenticator
{
    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task SignInAsync(CancellationToken cancellationToken = default) => throw Error();

    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) => throw Error();

    private SyncAuthenticationException Error() => new("Google sign-in is not configured for this platform: " + reason);
}
