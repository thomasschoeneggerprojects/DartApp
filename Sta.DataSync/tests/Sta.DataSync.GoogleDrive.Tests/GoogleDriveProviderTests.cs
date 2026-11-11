using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Sta.DataSync.Providers.GoogleDrive.Services;
using Sta.DataSync.Testing;

namespace Sta.DataSync.GoogleDrive.Tests;

public sealed class GoogleDriveProviderTests
{
    private const string Scope = "Sta.DataSync/TestApp/data";

    private static (GoogleDriveSyncProvider Provider, FakeDriveServer Server, StaticTokenProvider Tokens) Create()
    {
        var server = new FakeDriveServer();
        var tokens = new StaticTokenProvider();
        var provider = new GoogleDriveSyncProvider(tokens, Options.Create(new GoogleDriveProviderOptions()), null, server.ClientFactory);
        return (provider, server, tokens);
    }

    private static Task<RemoteSyncItem> UploadAsync(GoogleDriveSyncProvider provider, string path, string text)
        => provider.UploadAsync(new RemoteUpload(path), new MemoryStream(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task Upload_download_and_metadata_round_trip()
    {
        (GoogleDriveSyncProvider provider, _, _) = Create();

        RemoteSyncItem stored = await UploadAsync(provider, $"{Scope}/objects/abc", "hello drive");
        await using Stream content = await provider.DownloadAsync(stored);
        using var reader = new StreamReader(content);

        Assert.Equal("hello drive", await reader.ReadToEndAsync());
        Assert.Equal($"{Scope}/objects/abc", stored.Path);
        Assert.Equal(11, stored.Length);
        Assert.StartsWith(ContentHash.Prefix, stored.ContentHash, StringComparison.Ordinal);
        Assert.NotNull(stored.CreatedUtc);
    }

    [Fact]
    public async Task Uploading_an_existing_path_updates_the_same_file()
    {
        (GoogleDriveSyncProvider provider, FakeDriveServer server, _) = Create();

        RemoteSyncItem first = await UploadAsync(provider, $"{Scope}/objects/x", "one");
        RemoteSyncItem second = await UploadAsync(provider, $"{Scope}/objects/x", "two!");

        Assert.Equal(first.ProviderItemId, second.ProviderItemId);
        Assert.NotEqual(first.Version, second.Version);
        Assert.Single(server.Names);
    }

    [Fact]
    public async Task Listing_follows_all_pages_and_filters_by_scope()
    {
        (GoogleDriveSyncProvider provider, FakeDriveServer server, _) = Create();
        for (int i = 0; i < 8; i++) await UploadAsync(provider, $"{Scope}/objects/{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await UploadAsync(provider, "Sta.DataSync/TestApp/other/objects/x", "other scope");

        IReadOnlyList<RemoteSyncItem> items = await provider.ListItemsAsync(Scope + "/");

        Assert.Equal(8, items.Count);
        Assert.True(server.Requests.Count(r => r == "GET /drive/v3/files") >= 3);
    }

    [Fact]
    public async Task Missing_items_map_to_not_found()
    {
        (GoogleDriveSyncProvider provider, _, _) = Create();

        Assert.Null(await provider.GetItemAsync($"{Scope}/nothing"));
        await Assert.ThrowsAsync<SyncItemNotFoundException>(() => provider.DownloadAsync(new RemoteSyncItem($"{Scope}/x", "missing", "1", 0)));
        await provider.DeleteAsync(new RemoteSyncItem($"{Scope}/x", "missing", "1", 0)); // deleting a missing item succeeds
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_once()
    {
        (GoogleDriveSyncProvider provider, FakeDriveServer server, StaticTokenProvider tokens) = Create();
        int calls = 0;
        server.Intercept = request => Interlocked.Increment(ref calls) == 1 ? FakeDriveServer.Error(HttpStatusCode.Unauthorized, "authError") : null;

        IReadOnlyList<RemoteSyncItem> items = await provider.ListItemsAsync(Scope + "/");

        Assert.Empty(items);
        Assert.Equal(1, tokens.Refreshes);
        Assert.Contains("Bearer token-1", server.AuthorizationHeaders);
    }

    [Fact]
    public async Task Revoked_credentials_map_to_authentication_failure()
    {
        (GoogleDriveSyncProvider provider, FakeDriveServer server, _) = Create();
        server.Intercept = _ => FakeDriveServer.Error(HttpStatusCode.Unauthorized, "authError");

        await Assert.ThrowsAsync<SyncAuthenticationException>(() => provider.ListItemsAsync(Scope + "/"));
    }

    [Fact]
    public async Task Missing_sign_in_surfaces_as_authentication_failure()
    {
        (GoogleDriveSyncProvider provider, _, StaticTokenProvider tokens) = Create();
        tokens.Failure = new SyncAuthenticationException("No Google account is connected.");

        await Assert.ThrowsAsync<SyncAuthenticationException>(() => provider.ListItemsAsync(Scope + "/"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded", typeof(SyncTransientException))]
    [InlineData(HttpStatusCode.Forbidden, "userRateLimitExceeded", typeof(SyncTransientException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, "backendError", typeof(SyncTransientException))]
    [InlineData(HttpStatusCode.Forbidden, "storageQuotaExceeded", typeof(SyncProviderException))]
    [InlineData(HttpStatusCode.Forbidden, "insufficientPermissions", typeof(SyncAuthenticationException))]
    public async Task Api_errors_are_classified(HttpStatusCode status, string reason, Type expected)
    {
        (GoogleDriveSyncProvider provider, FakeDriveServer server, _) = Create();
        server.Intercept = _ => FakeDriveServer.Error(status, reason);

        Exception error = await Assert.ThrowsAnyAsync<SyncException>(() => provider.ListItemsAsync(Scope + "/"));

        Assert.IsType(expected, error);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task Failed_upload_reports_a_transient_error()
    {
        (GoogleDriveSyncProvider provider, FakeDriveServer server, _) = Create();
        server.Intercept = request => request.Method == HttpMethod.Put ? FakeDriveServer.Error(HttpStatusCode.ServiceUnavailable, "backendError") : null;

        await Assert.ThrowsAsync<SyncTransientException>(() => UploadAsync(provider, $"{Scope}/objects/a", "x"));
    }

    [Fact]
    public async Task Change_tokens_report_only_relevant_changes()
    {
        (GoogleDriveSyncProvider provider, _, _) = Create();
        string start = await provider.GetStartTokenAsync();
        await UploadAsync(provider, "Sta.DataSync/TestApp/other/objects/x", "elsewhere");

        RemoteChangeSet unrelated = await provider.GetChangesAsync(start, Scope + "/");
        await UploadAsync(provider, $"{Scope}/objects/y", "here");
        RemoteChangeSet related = await provider.GetChangesAsync(unrelated.NextToken, Scope + "/");
        RemoteChangeSet none = await provider.GetChangesAsync(related.NextToken, Scope + "/");

        Assert.False(unrelated.HasChanges);
        Assert.True(related.HasChanges);
        Assert.False(none.HasChanges);
    }

    [Fact]
    public async Task Expired_change_token_requests_a_full_listing()
    {
        (GoogleDriveSyncProvider provider, _, _) = Create();

        await Assert.ThrowsAsync<SyncConcurrencyException>(() => provider.GetChangesAsync("not-a-token", Scope + "/"));
    }

    [Fact]
    public async Task Conditional_writes_are_refused_because_drive_cannot_honor_them()
    {
        (GoogleDriveSyncProvider provider, _, _) = Create();

        Assert.False(provider.Capabilities.HasFlag(SyncProviderCapabilities.ConditionalWrites));
        Assert.True(provider.Capabilities.HasFlag(SyncProviderCapabilities.ServerCreationOrder));
        await Assert.ThrowsAsync<SyncConfigurationException>(() =>
            provider.UploadAsync(new RemoteUpload($"{Scope}/manifest.json", ExpectedVersion: "3"), new MemoryStream()));
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        var options = Options.Create(new GoogleDriveProviderOptions { UploadChunkSize = 1000, PageSize = 0 });

        Assert.Throws<SyncConfigurationException>(() => new GoogleDriveSyncProvider(new StaticTokenProvider(), options));
    }
}
