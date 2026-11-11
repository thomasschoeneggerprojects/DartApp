#if ANDROID
using Android.App;
using Android.Content;
using Android.Gms.Auth.Api.Identity;
using Android.Gms.Common.Apis;
using Android.Gms.Extensions;
using Sta.DataSync.Providers.GoogleDrive.Authentication;

namespace Sta.DataSync.Maui.Authentication;

/// <summary>
/// Android sign-in with Google Identity Services (<c>AuthorizationClient</c>), Google's recommended way to obtain Drive
/// access tokens on Android. No client secret or refresh token is handled by the app: Play services keeps the grant
/// and issues short-lived access tokens silently once the user consented.
/// </summary>
/// <remarks>
/// Requires an "Android" OAuth client in the Google Cloud project with the app's package name and signing certificate
/// SHA-1. Consent screens need a foreground activity; <see cref="HandleActivityResult"/> must be wired to
/// <c>OnActivityResult</c> (done by <c>UseDataSync()</c>).
/// </remarks>
public sealed class AndroidGoogleAuthenticator(MauiGoogleAuthOptions options) : IGoogleAuthenticator, IDisposable
{
    private const int RequestCode = 0x5DA7;
    private const string ConnectedKey = "sta.datasync.google.connected";
    private static readonly TimeSpan AssumedLifetime = TimeSpan.FromMinutes(50);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<AuthorizationResult>? _pending;
    private string? _token;
    private DateTimeOffset _expires;

    /// <inheritdoc />
    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default) => Task.FromResult(Preferences.Default.Get(ConnectedKey, false));

    /// <inheritdoc />
    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        string token = await AuthorizeAsync(interactive: true, cancellationToken).ConfigureAwait(false);
        Remember(token);
        Preferences.Default.Set(ConnectedKey, true);
    }

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!Preferences.Default.Get(ConnectedKey, false)) throw new SyncAuthenticationException("No Google account is connected.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _token is not null && _expires > DateTimeOffset.UtcNow) return _token;
            string token = await AuthorizeAsync(interactive: false, cancellationToken).ConfigureAwait(false);
            Remember(token);
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        string? token = _token;
        _token = null;
        Preferences.Default.Set(ConnectedKey, false);
        if (token is null) return;

        try
        {
            using var http = new HttpClient();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using HttpResponseMessage response = await http.PostAsync(new GoogleOAuthOptions().RevocationEndpoint, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Best effort: the local connection is removed regardless.
        }
    }

    /// <summary>Completes a consent flow; call from <c>Activity.OnActivityResult</c>.</summary>
    public void HandleActivityResult(Activity activity, int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != RequestCode || _pending is null) return;
        if (resultCode != Result.Ok || data is null)
        {
            _pending.TrySetException(new OperationCanceledException("Google sign-in was canceled."));
            return;
        }

        try
        {
            _pending.TrySetResult(Identity.GetAuthorizationClient(activity).GetAuthorizationResultFromIntent(data));
        }
        catch (ApiException ex)
        {
            _pending.TrySetException(new SyncAuthenticationException("Google authorization failed.", ex));
        }
    }

    private async Task<string> AuthorizeAsync(bool interactive, CancellationToken cancellationToken)
    {
        Activity? activity = Platform.CurrentActivity;
        Context context = (Context?)activity ?? Platform.AppContext;
        var request = AuthorizationRequest.InvokeBuilder()
            .SetRequestedScopes(options.Scopes.Select(s => new Scope(s)).ToList())
            .Build();

        AuthorizationResult result;
        try
        {
            result = await Identity.GetAuthorizationClient(context).Authorize(request).AsAsync<AuthorizationResult>().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            throw new SyncAuthenticationException("Google authorization failed.", ex);
        }

        if (result.HasResolution)
        {
            if (!interactive || activity is null) throw new SyncAuthenticationException("Google needs the user's consent again; connect the account in the app.");
            result = await ResolveAsync(activity, result, cancellationToken).ConfigureAwait(false);
        }

        return result.AccessToken ?? throw new SyncAuthenticationException("Google returned no access token.");
    }

    private async Task<AuthorizationResult> ResolveAsync(Activity activity, AuthorizationResult result, CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        await using CancellationTokenRegistration registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        await MainThread.InvokeOnMainThreadAsync(() =>
            activity.StartIntentSenderForResult(result.PendingIntent!.IntentSender, RequestCode, null, 0, 0, 0)).ConfigureAwait(false);
        try
        {
            return await pending.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending = null;
        }
    }

    /// <summary>Releases the token lock.</summary>
    public void Dispose() => _gate.Dispose();

    private void Remember(string token)
    {
        _token = token;
        _expires = DateTimeOffset.UtcNow + AssumedLifetime;
    }
}
#endif
