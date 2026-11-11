using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Sta.DataSync.Providers.GoogleDrive.Authentication;

/// <summary>
/// OAuth 2.0 for installed applications (authorization code + PKCE S256, RFC 7636/8252) with token refresh and
/// revocation. The browser step and token storage are injected, so the same class serves desktop and mobile platforms.
/// </summary>
public sealed partial class PkceGoogleAuthenticator : IGoogleAuthenticator, IDisposable
{
    private readonly GoogleOAuthOptions _options;
    private readonly IOAuthBrowser _browser;
    private readonly IGoogleTokenStore _store;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GoogleTokenSet? _cached;

    /// <summary>Creates the authenticator.</summary>
    public PkceGoogleAuthenticator(
        IOptions<GoogleOAuthOptions> options,
        IOAuthBrowser browser,
        IGoogleTokenStore store,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        ILogger<PkceGoogleAuthenticator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        IReadOnlyList<string> errors = _options.Validate();
        if (errors.Count > 0) throw new SyncConfigurationException("Invalid Google OAuth options: " + string.Join(" ", errors));

        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _time = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default)
        => (_cached ?? await _store.LoadAsync(cancellationToken).ConfigureAwait(false)) is not null;

    /// <inheritdoc />
    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        OAuthBrowserResult result = await _browser.AuthorizeAsync(redirect => BuildAuthorizationUrl(redirect, challenge, state), cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> query = ParseQuery(result.CallbackUri.Query);
        if (query.TryGetValue("error", out string? error))
        {
            LogAuthenticationFailed(_logger, error);
            throw error == "access_denied"
                ? new SyncAuthenticationException("Access to Google Drive was denied.")
                : new SyncAuthenticationException($"Google sign-in failed ({error}).");
        }

        if (!query.TryGetValue("state", out string? returnedState) || returnedState != state)
        {
            throw new SyncAuthenticationException("Google sign-in returned an unexpected state; the attempt was rejected.");
        }

        if (!query.TryGetValue("code", out string? code)) throw new SyncAuthenticationException("Google sign-in returned no authorization code.");

        GoogleTokenSet tokens = await RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = result.RedirectUri.ToString(),
        }, previousRefreshToken: null, cancellationToken).ConfigureAwait(false);
        await StoreAsync(tokens, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GoogleTokenSet tokens = _cached ??= await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new SyncAuthenticationException("No Google account is connected.");
            if (!forceRefresh && tokens.ExpiresUtc - _options.RefreshMargin > _time.GetUtcNow()) return tokens.AccessToken;
            if (tokens.RefreshToken is null) throw new SyncAuthenticationException("The Google session expired; sign in again.");

            GoogleTokenSet refreshed = await RequestTokensAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken,
            }, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
            await _store.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
            _cached = refreshed;
            return refreshed.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        GoogleTokenSet? tokens = _cached ?? await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (tokens is not null)
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokens.RefreshToken ?? tokens.AccessToken });
                using HttpResponseMessage response = await _http.PostAsync(_options.RevocationEndpoint, content, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // Revocation is best effort; local tokens are deleted regardless.
                LogRevocationFailed(_logger, ex.Message);
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached = null;
            await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the HTTP client when owned.</summary>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _gate.Dispose();
    }

    private Uri BuildAuthorizationUrl(Uri redirect, string challenge, string state)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirect.ToString(),
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', _options.Scopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        };
        string query = string.Join('&', parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return new Uri(_options.AuthorizationEndpoint + "?" + query);
    }

    private async Task<GoogleTokenSet> RequestTokensAsync(Dictionary<string, string> form, string? previousRefreshToken, CancellationToken cancellationToken)
    {
        form["client_id"] = _options.ClientId;
        if (!string.IsNullOrEmpty(_options.ClientSecret)) form["client_secret"] = _options.ClientSecret;

        HttpResponseMessage response;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            response = await _http.PostAsync(_options.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncTransientException("The Google token service is unreachable.", ex);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return ParseTokens(body, previousRefreshToken);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new SyncTransientException($"The Google token service returned {(int)response.StatusCode}.");
            }

            string error = ReadError(body);
            LogAuthenticationFailed(_logger, error);
            if (error == "invalid_grant") await ForgetAsync(cancellationToken).ConfigureAwait(false);
            throw new SyncAuthenticationException($"Google rejected the credentials ({error}); sign in again.");
        }
    }

    private GoogleTokenSet ParseTokens(string body, string? previousRefreshToken)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string accessToken = root.GetProperty("access_token").GetString() ?? throw new SyncAuthenticationException("The token response has no access token.");
        int expiresIn = root.TryGetProperty("expires_in", out JsonElement e) && e.TryGetInt32(out int seconds) ? seconds : 3600;
        string? refresh = root.TryGetProperty("refresh_token", out JsonElement r) ? r.GetString() : previousRefreshToken;
        string? scope = root.TryGetProperty("scope", out JsonElement s) ? s.GetString() : null;
        return new GoogleTokenSet(accessToken, refresh, _time.GetUtcNow().AddSeconds(expiresIn), scope);
    }

    private static string ReadError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out JsonElement error) ? error.GetString() ?? "unknown" : "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }

    private async Task StoreAsync(GoogleTokenSet tokens, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(tokens, cancellationToken).ConfigureAwait(false);
            _cached = tokens;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ForgetAsync(CancellationToken cancellationToken)
    {
        _cached = null;
        await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            string value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Google authentication failed: {Error}")]
    private static partial void LogAuthenticationFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Debug, Message = "Revoking the Google grant failed: {Reason}")]
    private static partial void LogRevocationFailed(ILogger logger, string reason);
}

/// <summary>
/// Desktop sign-in (Windows, macOS, Linux): opens the system browser and receives the redirect on a loopback address
/// (<c>http://127.0.0.1:{port}/</c>), as recommended for installed apps. Requires a "Desktop app" OAuth client.
/// Not usable on Android or iOS.
/// </summary>
public sealed class LoopbackOAuthBrowser : IOAuthBrowser
{
    private readonly Func<Uri, Task> _openBrowser;

    /// <summary>Creates the browser; <paramref name="openBrowser"/> overrides how the URL is opened (default: system browser).</summary>
    public LoopbackOAuthBrowser(Func<Uri, Task>? openBrowser = null)
    {
        _openBrowser = openBrowser ?? OpenSystemBrowserAsync;
    }

    /// <inheritdoc />
    public async Task<OAuthBrowserResult> AuthorizeAsync(Func<Uri, Uri> buildAuthorizationUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildAuthorizationUrl);
        var redirect = new Uri($"http://127.0.0.1:{FreePort().ToString(CultureInfo.InvariantCulture)}/");
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect.ToString());
        listener.Start();
        await using CancellationTokenRegistration stop = cancellationToken.Register(listener.Stop);

        await _openBrowser(buildAuthorizationUrl(redirect)).ConfigureAwait(false);
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Google sign-in was canceled.", ex, cancellationToken);
        }

        Uri callback = context.Request.Url ?? redirect;
        byte[] page = Encoding.UTF8.GetBytes("<html><body style=\"font-family:sans-serif\"><p>Sign-in finished. You can close this window.</p></body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page, cancellationToken).ConfigureAwait(false);
        context.Response.Close();
        return new OAuthBrowserResult(redirect, callback);
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private static Task OpenSystemBrowserAsync(Uri url)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
