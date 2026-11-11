using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sta.DataSync.Providers.GoogleDrive.Authentication;
using Sta.DataSync.Providers.GoogleDrive.Services;

namespace Sta.DataSync.IntegrationTests;

/// <summary>
/// Opt-in tests against the real Google Drive API. They run only when these environment variables are set
/// (never commit them): <c>STA_DATASYNC_GOOGLE_CLIENT_ID</c>, <c>STA_DATASYNC_GOOGLE_CLIENT_SECRET</c> (desktop client)
/// and <c>STA_DATASYNC_GOOGLE_REFRESH_TOKEN</c> (from a test account, scope drive.appdata).
/// See docs/google-drive-setup.md.
/// </summary>
public sealed class RealGoogleDriveTests
{
    [SkippableFact]
    public async Task Round_trip_through_the_real_app_data_folder()
    {
        string? clientId = Environment.GetEnvironmentVariable("STA_DATASYNC_GOOGLE_CLIENT_ID");
        string? secret = Environment.GetEnvironmentVariable("STA_DATASYNC_GOOGLE_CLIENT_SECRET");
        string? refresh = Environment.GetEnvironmentVariable("STA_DATASYNC_GOOGLE_REFRESH_TOKEN");
        Skip.If(string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(refresh), "Real Google Drive credentials are not configured.");

        var store = new EnvironmentTokenStore(new GoogleTokenSet("expired", refresh, DateTimeOffset.MinValue, null));
        using var authenticator = new PkceGoogleAuthenticator(
            Options.Create(new GoogleOAuthOptions { ClientId = clientId!, ClientSecret = secret }),
            new LoopbackOAuthBrowser(_ => throw new InvalidOperationException("Interactive sign-in is not available in tests.")),
            store);
        string applicationId = "IntegrationTest" + Guid.NewGuid().ToString("N")[..8];
        string root = Path.Combine(Path.GetTempPath(), "sta-datasync-real", applicationId);
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        await File.WriteAllTextAsync(Path.Combine(root, "a", "hello.json"), "{\"from\":\"integration test\"}");

        using ServiceProvider a = Build(authenticator, applicationId, Path.Combine(root, "a"), Path.Combine(root, "a-state.db3"));
        using ServiceProvider b = Build(authenticator, applicationId, Path.Combine(root, "b"), Path.Combine(root, "b-state.db3"));
        try
        {
            SyncResult up = await a.GetRequiredService<IDataSyncService>().SynchronizeAsync("files");
            SyncResult down = await b.GetRequiredService<IDataSyncService>().SynchronizeAsync("files");

            Assert.Equal(SyncResultStatus.Succeeded, up.Status);
            Assert.Equal(1, down.DownloadedCount);
            Assert.Equal("{\"from\":\"integration test\"}", await File.ReadAllTextAsync(Path.Combine(root, "b", "hello.json")));
        }
        finally
        {
            var provider = a.GetRequiredService<GoogleDriveSyncProvider>();
            foreach (RemoteSyncItem item in await provider.ListItemsAsync($"Sta.DataSync/{applicationId}/")) await provider.DeleteAsync(item);
        }
    }

    private static ServiceProvider Build(IGoogleAccessTokenProvider tokens, string applicationId, string directory, string statePath)
    {
        var services = new ServiceCollection();
        services.AddDataSync(o => o.ApplicationId = applicationId, statePath);
        services.AddSingleton(tokens);
        services.AddGoogleDriveSyncProvider();
        services.AddFileSyncSource("files", directory);
        return services.BuildServiceProvider();
    }

    private sealed class EnvironmentTokenStore(GoogleTokenSet initial) : IGoogleTokenStore
    {
        private GoogleTokenSet? _tokens = initial;

        public Task<GoogleTokenSet?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_tokens);

        public Task SaveAsync(GoogleTokenSet tokens, CancellationToken cancellationToken = default)
        {
            _tokens = tokens;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            _tokens = null;
            return Task.CompletedTask;
        }
    }
}
