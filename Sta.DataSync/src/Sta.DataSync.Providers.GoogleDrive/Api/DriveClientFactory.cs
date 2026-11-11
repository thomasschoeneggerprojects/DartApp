using System.Net.Http.Headers;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Sta.DataSync.Providers.GoogleDrive.Authentication;

namespace Sta.DataSync.Providers.GoogleDrive.Api;

/// <summary>
/// Creates the official <see cref="DriveService"/> with bearer-token authentication from an
/// <see cref="IGoogleAccessTokenProvider"/>. The client's own exponential backoff is disabled: retries happen in the
/// engine's retry policy, so they are bounded and visible in one place.
/// </summary>
internal static class DriveClientFactory
{
    public static DriveService Create(IGoogleAccessTokenProvider tokens, string applicationName, Google.Apis.Http.IHttpClientFactory? httpClientFactory = null)
        => new(new BaseClientService.Initializer
        {
            ApplicationName = applicationName,
            HttpClientInitializer = new AccessTokenInitializer(tokens),
            HttpClientFactory = httpClientFactory ?? new HttpClientFactory(),
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
            GZipEnabled = true,
        });

    /// <summary>Adds the access token to every request and refreshes it once after a 401 response.</summary>
    private sealed class AccessTokenInitializer(IGoogleAccessTokenProvider tokens)
        : IConfigurableHttpClientInitializer, IHttpExecuteInterceptor, IHttpUnsuccessfulResponseHandler
    {
        public void Initialize(ConfigurableHttpClient httpClient)
        {
            httpClient.MessageHandler.AddExecuteInterceptor(this);
            httpClient.MessageHandler.AddUnsuccessfulResponseHandler(this);
        }

        public async Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string token = await tokens.GetAccessTokenAsync(false, cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public async Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            if (args.Response.StatusCode != System.Net.HttpStatusCode.Unauthorized || args.CurrentFailedTry > 1) return false;
            string token = await tokens.GetAccessTokenAsync(true, args.CancellationToken).ConfigureAwait(false);
            args.Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return true;
        }
    }
}
