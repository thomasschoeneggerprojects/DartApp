using Sta.DataSync.Core.Tests.Fakes;

namespace Sta.DataSync.Core.Tests;

public sealed class SynchronizationTests
{
    [Fact]
    public async Task First_sync_uploads_all_local_items_and_another_device_receives_them()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        using var b = new Device(provider);
        a.Source.Set("settings.json", "{\"theme\":\"dark\"}");
        a.Source.Set("records/1.json", "one");

        SyncResult first = await a.SyncAsync();
        SyncResult second = await b.SyncAsync();

        Assert.Equal(SyncResultStatus.Succeeded, first.Status);
        Assert.Equal(2, first.UploadedCount);
        Assert.Equal(2, second.DownloadedCount);
        Assert.Equal("{\"theme\":\"dark\"}", b.Source.Get("settings.json"));
        Assert.Equal("one", b.Source.Get("records/1.json"));
    }

    [Fact]
    public async Task Sync_without_changes_transfers_nothing()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("a.txt", "x");
        await a.SyncAsync();
        int uploads = provider.Count("upload");

        SyncResult result = await a.SyncAsync();

        Assert.Equal(SyncResultStatus.NoChanges, result.Status);
        Assert.Equal(0, result.TransferredBytes);
        Assert.Equal(uploads, provider.Count("upload"));
    }

    [Fact]
    public async Task Change_feed_skips_remote_listing_when_nothing_changed()
    {
        var provider = new FakeProvider(SyncProviderCapabilities.ServerCreationOrder | SyncProviderCapabilities.ChangeFeed);
        using var a = new Device(provider);
        a.Source.Set("a.txt", "x");
        await a.SyncAsync();
        await a.SyncAsync(); // sees its own commit once
        int lists = provider.Count("list");

        SyncResult result = await a.SyncAsync();

        Assert.Equal(SyncResultStatus.NoChanges, result.Status);
        Assert.Equal(lists, provider.Count("list"));
    }

    [Fact]
    public async Task Unchanged_items_are_not_hashed_again()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("a.txt", "x");
        int reads = 0;
        a.Source.OnOpenRead = _ => Interlocked.Increment(ref reads);
        await a.SyncAsync();
        int afterFirst = reads;

        await a.SyncAsync();

        Assert.Equal(afterFirst, reads);
    }

    [Fact]
    public async Task Local_and_remote_modifications_propagate_both_ways()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        using var b = new Device(provider);
        a.Source.Set("a.txt", "v1");
        await a.SyncAsync();
        await b.SyncAsync();

        b.Source.Set("a.txt", "v2 from b");
        await b.SyncAsync();
        SyncResult result = await a.SyncAsync();

        Assert.Equal(1, result.DownloadedCount);
        Assert.Equal("v2 from b", a.Source.Get("a.txt"));
    }

    [Fact]
    public async Task Upload_only_never_applies_remote_changes()
    {
        var provider = new FakeProvider();
        using var writer = new Device(provider);
        using var uploader = new Device(provider, SyncMode.UploadOnly);
        writer.Source.Set("remote.txt", "from writer");
        await writer.SyncAsync();
        uploader.Source.Set("local.txt", "from uploader");

        SyncResult result = await uploader.SyncAsync();

        Assert.Null(uploader.Source.Get("remote.txt"));
        Assert.Equal(1, result.UploadedCount);
        Assert.Equal(1, result.SkippedCount);
        await writer.SyncAsync();
        Assert.Equal("from uploader", writer.Source.Get("local.txt"));
    }

    [Fact]
    public async Task Download_only_mirrors_the_destination_and_never_uploads()
    {
        var provider = new FakeProvider();
        using var writer = new Device(provider);
        using var mirror = new Device(provider, SyncMode.DownloadOnly);
        writer.Source.Set("a.txt", "original");
        await writer.SyncAsync();
        await mirror.SyncAsync();

        mirror.Source.Set("a.txt", "local edit");
        mirror.Source.Set("local-only.txt", "x");
        SyncResult result = await mirror.SyncAsync();

        Assert.Equal("original", mirror.Source.Get("a.txt"));
        Assert.Equal("x", mirror.Source.Get("local-only.txt"));
        Assert.Equal(0, result.UploadedCount);
        await writer.SyncAsync();
        Assert.Null(writer.Source.Get("local-only.txt"));
    }

    [Fact]
    public async Task Deletions_propagate_through_tombstones()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        using var b = new Device(provider);
        a.Source.Set("a.txt", "x");
        await a.SyncAsync();
        await b.SyncAsync();

        a.Source.Remove("a.txt");
        SyncResult deleted = await a.SyncAsync();
        SyncResult applied = await b.SyncAsync();

        Assert.Equal(1, deleted.DeletedCount);
        Assert.Equal(1, applied.DeletedCount);
        Assert.Null(b.Source.Get("a.txt"));
    }

    [Fact]
    public async Task Tombstone_of_an_unknown_item_is_adopted_without_transfers()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("a.txt", "x");
        await a.SyncAsync();
        a.Source.Remove("a.txt");
        await a.SyncAsync();
        using var newcomer = new Device(provider);

        SyncResult result = await newcomer.SyncAsync();

        Assert.Equal(0, result.DownloadedCount);
        Assert.Null(newcomer.Source.Get("a.txt"));
    }

    [Fact]
    public async Task Item_missing_remotely_without_tombstone_is_restored_not_deleted()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("a.txt", "precious");
        await a.SyncAsync();
        foreach (string path in provider.Paths) await provider.DeleteAsync(new RemoteSyncItem(path, "x", "1", 0));

        SyncResult result = await a.SyncAsync();

        Assert.Equal("precious", a.Source.Get("a.txt"));
        Assert.Equal(1, result.UploadedCount);
    }

    [Fact]
    public async Task Identical_content_is_stored_once()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("one.txt", "same");
        a.Source.Set("two.txt", "same");

        await a.SyncAsync();

        Assert.Single(provider.Paths, p => p.Contains("/objects/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Conditional_write_providers_use_a_single_manifest()
    {
        var provider = new FakeProvider(SyncProviderCapabilities.ConditionalWrites);
        using var a = new Device(provider);
        using var b = new Device(provider);
        a.Source.Set("a.txt", "x");
        await a.SyncAsync();
        b.Source.Set("b.txt", "y");
        await b.SyncAsync();
        await a.SyncAsync();

        Assert.Contains(provider.Paths, p => p.EndsWith("/data/manifest.json", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Paths, p => p.Contains("/manifests/", StringComparison.Ordinal));
        Assert.Equal("y", a.Source.Get("b.txt"));
    }

    [Fact]
    public async Task Multiple_scopes_synchronize_independently()
    {
        var provider = new FakeProvider();
        var settings = new FakeSource("settings");
        var records = new FakeSource("records");
        var options = Microsoft.Extensions.Options.Options.Create(new DataSyncOptions { ApplicationId = "App", CommitVerificationDelay = TimeSpan.Zero });
        using var service = new DataSyncService(
            [new SyncScopeDefinition("settings", settings, provider), new SyncScopeDefinition("records", records, provider)],
            new FakeStateStore(),
            options);
        settings.Set("s.json", "s");
        records.Set("r.json", "r");

        SyncResult all = await service.SynchronizeAsync();
        SyncResult one = await service.SynchronizeAsync("records");

        Assert.Equal(2, all.ScopeResults.Count);
        Assert.Equal(2, all.UploadedCount);
        Assert.Equal(SyncResultStatus.NoChanges, one.Status);
        Assert.Contains(provider.Paths, p => p.StartsWith("Sta.DataSync/App/settings/", StringComparison.Ordinal));
        Assert.Contains(provider.Paths, p => p.StartsWith("Sta.DataSync/App/records/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parallel_requests_for_one_scope_never_overlap()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        for (int i = 0; i < 20; i++) a.Source.Set($"item{i}.txt", $"content {i}");

        SyncResult[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => a.SyncAsync())));

        Assert.All(results, r => Assert.Contains(r.Status, new[] { SyncResultStatus.Succeeded, SyncResultStatus.NoChanges }));
        Assert.Equal(1, a.Source.MaxConcurrentEnumerations);
        // Callers that arrive while a run is still queued share its result: count each run once.
        Assert.Equal(20, results.DistinctBy(r => r.SessionId).Sum(r => r.UploadedCount));
    }

    [Fact]
    public async Task Progress_is_reported()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("a.txt", "x");
        a.Source.Set("b.txt", "y");
        var reports = new List<SyncProgress>();

        await a.Service.SynchronizeAsync("data", new Progress<SyncProgress>(reports.Add));
        await Task.Delay(50);

        Assert.Contains(reports, p => p.TotalItems == 2 && p.CompletedItems == 2);
    }

    [Fact]
    public async Task Status_reports_last_success()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("a.txt", "x");
        await a.SyncAsync();

        SyncStatus status = await a.Service.GetStatusAsync();

        Assert.Equal(SyncActivity.Idle, status.Activity);
        Assert.NotNull(status.LastSuccessUtc);
        Assert.Null(Assert.Single(status.Scopes).LastError);
    }
}
