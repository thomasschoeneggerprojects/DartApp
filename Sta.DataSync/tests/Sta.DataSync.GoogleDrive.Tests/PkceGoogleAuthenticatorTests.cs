using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Sta.DataSync.Providers.GoogleDrive.Authentication;

namespace Sta.DataSync.GoogleDrive.Tests;

public sealed class PkceGoogleAuthenticatorTests
{
    private static (PkceGoogleAuthenticator Authenticator, MemoryTokenStore Store, TokenEndpoint Endpoint, FakeBrowser Browser) Create()
    {
        var store = new MemoryTokenStore();
        var endpoint = new TokenEndpoint();
        var browser = new FakeBrowser();
        var options = Options.Create(new GoogleOAuthOptions { ClientId = "client.apps.googleusercontent.com", ClientSecret = "from-config" });
        return (new PkceGoogleAuthenticator(options, browser, store, new HttpClient(endpoint)), store, endpoint, browser);
    }

    [Fact]
    public async Task Sign_in_uses_pkce_and_stores_tokens()
    {
        (PkceGoogleAuthenticator auth, MemoryTokenStore store, TokenEndpoint endpoint, FakeBrowser browser) = Create();
        endpoint.Respond = _ => Ok("""{"access_token":"a1","refresh_token":"r1","expires_in":3600,"scope":"https://www.googleapis.com/auth/drive.appdata"}""");

        await auth.SignInAsync();

        Assert.Contains("code_challenge_method=S256", browser.AuthorizationUrl!.Query, StringComparison.Ordinal);
        Assert.Contains("drive.appdata", Uri.UnescapeDataString(browser.AuthorizationUrl.Query), StringComparison.Ordinal);
        Assert.Contains("code_verifier=", endpoint.LastBody, StringComparison.Ordinal);
        Assert.Equal("r1", store.Tokens!.RefreshToken);
        Assert.Equal("a1", await auth.GetAccessTokenAsync());
        Assert.DoesNotContain("a1", store.Tokens.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mismatched_state_is_rejected()
    {
        (PkceGoogleAuthenticator auth, _, _, FakeBrowser browser) = Create();
        browser.OverrideState = "forged";

        await Assert.ThrowsAsync<SyncAuthenticationException>(() => auth.SignInAsync());
    }

    [Fact]
    public async Task Denied_consent_is_an_authentication_failure()
    {
        (PkceGoogleAuthenticator auth, _, _, FakeBrowser browser) = Create();
        browser.Error = "access_denied";

        await Assert.ThrowsAsync<SyncAuthenticationException>(() => auth.SignInAsync());
    }

    [Fact]
    public async Task Expired_tokens_are_refreshed_and_revoked_grants_require_sign_in()
    {
        (PkceGoogleAuthenticator auth, MemoryTokenStore store, TokenEndpoint endpoint, _) = Create();
        store.Tokens = new GoogleTokenSet("old", "r1", DateTimeOffset.UtcNow.AddMinutes(-1), null);
        endpoint.Respond = _ => Ok("""{"access_token":"fresh","expires_in":3600}""");

        Assert.Equal("fresh", await auth.GetAccessTokenAsync());
        Assert.Equal("r1", store.Tokens!.RefreshToken);

        endpoint.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
        await Assert.ThrowsAsync<SyncAuthenticationException>(() => auth.GetAccessTokenAsync(forceRefresh: true));
        Assert.Null(store.Tokens);
        Assert.False(await auth.IsSignedInAsync());
    }

    [Fact]
    public async Task Token_service_outages_are_transient()
    {
        (PkceGoogleAuthenticator auth, MemoryTokenStore store, TokenEndpoint endpoint, _) = Create();
        store.Tokens = new GoogleTokenSet("old", "r1", DateTimeOffset.UtcNow.AddMinutes(-1), null);
        endpoint.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<SyncTransientException>(() => auth.GetAccessTokenAsync());
        Assert.NotNull(store.Tokens);
    }

    [Fact]
    public async Task Sign_out_revokes_and_deletes_tokens()
    {
        (PkceGoogleAuthenticator auth, MemoryTokenStore store, TokenEndpoint endpoint, _) = Create();
        store.Tokens = new GoogleTokenSet("a", "r", DateTimeOffset.UtcNow.AddHours(1), null);
        endpoint.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

        await auth.SignOutAsync();

        Assert.Null(store.Tokens);
        Assert.EndsWith("/revoke", endpoint.LastUri!.AbsolutePath, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SyncAuthenticationException>(() => auth.GetAccessTokenAsync());
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class MemoryTokenStore : IGoogleTokenStore
    {
        public GoogleTokenSet? Tokens { get; set; }

        public Task<GoogleTokenSet?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Tokens);

        public Task SaveAsync(GoogleTokenSet tokens, CancellationToken cancellationToken = default)
        {
            Tokens = tokens;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            Tokens = null;
            return Task.CompletedTask;
        }
    }

    private sealed class TokenEndpoint : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"x","expires_in":3600}""") };

        public string LastBody { get; private set; } = string.Empty;

        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return Respond(request);
        }
    }

    private sealed class FakeBrowser : IOAuthBrowser
    {
        public Uri? AuthorizationUrl { get; private set; }

        public string? OverrideState { get; set; }

        public string? Error { get; set; }

        public Task<OAuthBrowserResult> AuthorizeAsync(Func<Uri, Uri> buildAuthorizationUrl, CancellationToken cancellationToken = default)
        {
            var redirect = new Uri("http://127.0.0.1:5555/");
            AuthorizationUrl = buildAuthorizationUrl(redirect);
            string state = OverrideState ?? AuthorizationUrl.Query.Split('&').First(p => p.StartsWith("state=", StringComparison.Ordinal))[6..];
            string query = Error is null ? $"?code=auth-code&state={state}" : $"?error={Error}&state={state}";
            return Task.FromResult(new OAuthBrowserResult(redirect, new Uri(redirect, query)));
        }
    }
}
