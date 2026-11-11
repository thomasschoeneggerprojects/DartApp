namespace Sta.DataSync.Providers.GoogleDrive.Authentication;

/// <summary>Supplies OAuth access tokens for Google APIs. Platform-independent; implemented per platform.</summary>
public interface IGoogleAccessTokenProvider
{
    /// <summary>Returns a valid access token, refreshing it when expired or when <paramref name="forceRefresh"/> is set.</summary>
    /// <exception cref="SyncAuthenticationException">Not signed in, or the grant was revoked (user must sign in again).</exception>
    /// <exception cref="SyncTransientException">The token service is temporarily unreachable.</exception>
    Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

/// <summary>Interactive Google account connection (sign in, sign out) on top of <see cref="IGoogleAccessTokenProvider"/>.</summary>
public interface IGoogleAuthenticator : IGoogleAccessTokenProvider
{
    /// <summary><see langword="true"/> when credentials are stored (they may still be revoked server-side).</summary>
    Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the interactive sign-in. Cancellation (token or user closing the browser) throws <see cref="OperationCanceledException"/>.</summary>
    /// <exception cref="SyncAuthenticationException">The user denied access or the exchange failed.</exception>
    Task SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Revokes the grant (best effort) and deletes stored tokens.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>Stored OAuth tokens. Never logged.</summary>
/// <param name="AccessToken">Short-lived access token.</param>
/// <param name="RefreshToken">Long-lived refresh token, if the flow issued one.</param>
/// <param name="ExpiresUtc">Access token expiry.</param>
/// <param name="Scope">Granted scopes.</param>
public sealed record GoogleTokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresUtc, string? Scope)
{
    /// <inheritdoc />
    public override string ToString() => $"GoogleTokenSet(expires {ExpiresUtc:O}, refresh token: {(RefreshToken is null ? "no" : "yes")})";
}

/// <summary>Persists tokens in platform-protected storage (Android Keystore, Windows DPAPI, iOS Keychain, ...).</summary>
public interface IGoogleTokenStore
{
    /// <summary>Loads stored tokens, or <see langword="null"/>.</summary>
    Task<GoogleTokenSet?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores tokens.</summary>
    Task SaveAsync(GoogleTokenSet tokens, CancellationToken cancellationToken = default);

    /// <summary>Deletes stored tokens.</summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Shows the authorization page and captures the redirect. Platform-specific: loopback listener on desktops,
/// <c>WebAuthenticator</c> or a custom tab on mobile.
/// </summary>
public interface IOAuthBrowser
{
    /// <summary>
    /// Chooses a redirect URI, asks <paramref name="buildAuthorizationUrl"/> for the authorization URL for it, shows it and
    /// returns the full redirect URI (with query) once the user finished.
    /// </summary>
    Task<OAuthBrowserResult> AuthorizeAsync(Func<Uri, Uri> buildAuthorizationUrl, CancellationToken cancellationToken = default);
}

/// <summary>Result of <see cref="IOAuthBrowser.AuthorizeAsync"/>.</summary>
/// <param name="RedirectUri">The redirect URI that was registered with the authorization request.</param>
/// <param name="CallbackUri">The URI the browser was redirected to, including <c>code</c>/<c>state</c> or <c>error</c>.</param>
public sealed record OAuthBrowserResult(Uri RedirectUri, Uri CallbackUri);

/// <summary>OAuth client settings. Supply them from configuration; never hardcode secrets.</summary>
public sealed class GoogleOAuthOptions
{
    /// <summary>Least-privilege scope: only the application's hidden app data folder.</summary>
    public const string AppDataScope = "https://www.googleapis.com/auth/drive.appdata";

    /// <summary>OAuth client id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Client secret of "Desktop app" OAuth clients (Google requires it for the code exchange, but it is not confidential
    /// in installed apps). Leave empty for client types without a secret.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>Requested scopes; defaults to <see cref="AppDataScope"/> only.</summary>
    public IList<string> Scopes { get; } = [AppDataScope];

    /// <summary>Authorization endpoint.</summary>
    public Uri AuthorizationEndpoint { get; set; } = new("https://accounts.google.com/o/oauth2/v2/auth");

    /// <summary>Token endpoint.</summary>
    public Uri TokenEndpoint { get; set; } = new("https://oauth2.googleapis.com/token");

    /// <summary>Revocation endpoint.</summary>
    public Uri RevocationEndpoint { get; set; } = new("https://oauth2.googleapis.com/revoke");

    /// <summary>Access tokens are refreshed this long before they expire.</summary>
    public TimeSpan RefreshMargin { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Returns validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ClientId)) errors.Add("ClientId is required.");
        if (Scopes.Count == 0) errors.Add("At least one scope is required.");
        foreach (Uri endpoint in new[] { AuthorizationEndpoint, TokenEndpoint, RevocationEndpoint })
        {
            if (endpoint is null || endpoint.Scheme != Uri.UriSchemeHttps) errors.Add("OAuth endpoints must use HTTPS.");
        }

        return errors;
    }
}
