using System.Text;
using Microsoft.Extensions.Options;
using Sta.DataSync.Core.Tests.Fakes;

namespace Sta.DataSync.Core.Tests;

public sealed class ReliabilityTests
{
    [Fact]
    public async Task Interrupted_download_is_not_marked_synchronized_and_completes_later()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        using var b = new Device(provider);
        a.Source.Set("big.bin", "payload");
        a.Source.Set("small.txt", "s");
        await a.SyncAsync();
        provider.Fault = (op, path) => op == "download" && path.Contains("/objects/", StringComparison.Ordinal)
            ? new SyncProviderException("connection reset permanently")
            : null;

        SyncResult broken = await b.SyncAsync();
        string? afterFailure = b.Source.Get("big.bin");
        provider.Fault = null;
        SyncResult repaired = await b.SyncAsync();

        Assert.NotEqual(SyncResultStatus.Succeeded, broken.Status);
        Assert.Equal(2, broken.Failures.Count);
        Assert.Null(afterFailure);
        Assert.Equal(2, repaired.DownloadedCount);
        Assert.Equal("payload", b.Source.Get("big.bin"));
    }

    [Fact]
    public async Task Transient_failures_are_retried()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("a.txt", "x");
        int failures = 0;
        provider.Fault = (op, _) => op == "upload" && Interlocked.Increment(ref failures) <= 2 ? new SyncTransientException("503") : null;

        SyncResult result = await a.SyncAsync();

        Assert.Equal(SyncResultStatus.Succeeded, result.Status);
        Assert.True(failures >= 3);
    }

    [Fact]
    public async Task Permanent_failures_are_not_retried()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("a.txt", "x");
        int attempts = 0;
        provider.Fault = (op, path) => op == "upload" && path.Contains("/objects/", StringComparison.Ordinal) && Interlocked.Increment(ref attempts) > 0
            ? new SyncProviderException("quota exceeded")
            : null;

        SyncResult result = await a.SyncAsync();

        Assert.Equal(1, attempts);
        SyncFailure failure = Assert.Single(result.Failures);
        Assert.False(failure.IsTransient);
    }

    [Fact]
    public async Task Authentication_failure_stops_the_run_without_retries()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("a.txt", "x");
        provider.Fault = (op, _) => op == "list" ? new SyncAuthenticationException("token revoked") : null;

        SyncResult result = await a.SyncAsync();

        Assert.Equal(SyncResultStatus.AuthenticationRequired, result.Status);
        Assert.Equal(1, provider.Count("list"));
        SyncStatus status = await a.Service.GetStatusAsync();
        Assert.Equal("token revoked", status.Scopes[0].LastError);
    }

    [Fact]
    public async Task Cancellation_stops_the_run_and_keeps_unfinished_items_pending()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider, configure: o => o.MaxParallelTransfers = 1);
        for (int i = 0; i < 10; i++) a.Source.Set($"{i}.txt", $"content {i}");
        using var cts = new CancellationTokenSource();
        int uploads = 0;
        provider.Fault = (op, path) =>
        {
            if (op == "upload" && path.Contains("/objects/", StringComparison.Ordinal) && Interlocked.Increment(ref uploads) == 3) cts.Cancel();
            return null;
        };

        SyncResult canceled = await a.SyncAsync(cts.Token);
        provider.Fault = null;
        SyncResult resumed = await a.SyncAsync();

        Assert.Equal(SyncResultStatus.Canceled, canceled.Status);
        Assert.True(canceled.UploadedCount < 10);
        Assert.Equal(10, canceled.UploadedCount + resumed.UploadedCount);
    }

    [Fact]
    public async Task Content_changed_during_upload_is_rejected_and_never_committed()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        using var b = new Device(provider);
        a.Source.Set("a.txt", "version 1");
        bool changed = false;
        int opens = 0;
        a.Source.OnOpenRead = id =>
        {
            // First open hashes the content; the second (the upload) sees a newer version.
            if (Interlocked.Increment(ref opens) == 1 && !changed)
            {
                changed = true;
                a.Source.Set(id, "version 2 written during sync");
            }
        };

        SyncResult first = await a.SyncAsync();
        a.Source.OnOpenRead = null;
        await a.SyncAsync();
        await b.SyncAsync();

        Assert.Single(first.Failures);
        Assert.Equal("version 2 written during sync", b.Source.Get("a.txt"));
    }

    [Fact]
    public async Task Corrupt_downloads_never_replace_local_data()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        using var b = new Device(provider);
        a.Source.Set("a.txt", "v1");
        await a.SyncAsync();
        await b.SyncAsync();
        a.Source.Set("a.txt", "v2");
        await a.SyncAsync();
        provider.TamperDownload = (path, bytes) => path.Contains("/objects/", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("evil") : bytes;

        SyncResult result = await b.SyncAsync();

        Assert.Single(result.Failures);
        Assert.Equal("v1", b.Source.Get("a.txt"));
    }

    [Fact]
    public async Task Crash_after_remote_commit_is_recovered_without_duplicate_uploads()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("a.txt", "x");
        a.State.CommitFault = commit => commit.CompleteJournalSession is not null && commit.UpsertItems.Count > 0
            ? new IOException("process killed")
            : null;

        SyncResult crashed = await a.SyncAsync();
        Assert.Equal(SyncResultStatus.Failed, crashed.Status);
        Assert.NotEmpty(a.State.Journal);

        a.State.CommitFault = null;
        int uploadsBefore = provider.Count("upload");
        SyncResult recovered = await a.SyncAsync();

        Assert.Empty(a.State.Journal);
        Assert.Equal(0, recovered.UploadedCount);
        Assert.Equal(uploadsBefore, provider.Count("upload"));
    }

    [Fact]
    public async Task Losing_a_concurrent_commit_replans_without_losing_either_update()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        using var b = new Device(provider);
        a.Source.Set("base.txt", "base");
        await a.SyncAsync();
        await b.SyncAsync();

        // While B writes revision 2, device A's revision 2 appears with an earlier server time.
        a.Source.Set("from-a.txt", "A");
        bool injected = false;
        provider.BeforeUpload = upload =>
        {
            if (!injected && upload.Path.Contains("/manifests/000000000002-", StringComparison.Ordinal) && upload.Path.Contains(b.State.DeviceId, StringComparison.Ordinal))
            {
                injected = true;
                a.SyncAsync().GetAwaiter().GetResult();
            }
        };
        b.Source.Set("from-b.txt", "B");

        SyncResult result = await b.SyncAsync();
        provider.BeforeUpload = null;
        await a.SyncAsync();

        Assert.True(injected);
        Assert.Equal(SyncResultStatus.Succeeded, result.Status);
        Assert.Equal("A", b.Source.Get("from-a.txt"));
        Assert.Equal("B", a.Source.Get("from-b.txt"));
    }

    [Fact]
    public async Task A_commit_that_lost_after_the_fact_is_detected_and_replanned()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        a.Source.Set("base.txt", "base");
        await a.SyncAsync();
        a.Source.Set("mine.txt", "important");
        await a.SyncAsync(); // revision 2, believed to have won

        // An eventually consistent listing hid an earlier-created revision 2 from another device.
        string revision2 = provider.Paths.Single(p => p.Contains("/manifests/000000000002-", StringComparison.Ordinal));
        string revision1 = provider.Paths.Single(p => p.Contains("/manifests/000000000001-", StringComparison.Ordinal));
        string competing = Encoding.UTF8.GetString(provider.Read(revision1)).Replace("\"revision\":1", "\"revision\":2", StringComparison.Ordinal);
        provider.Put(revision2.Replace(a.State.DeviceId, "otherdev", StringComparison.Ordinal), Encoding.UTF8.GetBytes(competing), created: DateTimeOffset.UnixEpoch);

        SyncResult result = await a.SyncAsync();
        using var b = new Device(provider);
        await b.SyncAsync();

        Assert.Equal(SyncResultStatus.Succeeded, result.Status);
        Assert.Equal("important", b.Source.Get("mine.txt"));
    }

    [Fact]
    public async Task Newer_manifest_schema_is_refused_and_not_overwritten()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        provider.Put("Sta.DataSync/TestApp/data/manifests/000000000001-future-00000000.json", Encoding.UTF8.GetBytes("{\"schemaVersion\":99,\"revision\":1,\"items\":[]}"));
        a.Source.Set("a.txt", "x");

        SyncResult result = await a.SyncAsync();

        Assert.Equal(SyncResultStatus.Failed, result.Status);
        Assert.Contains("schema", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(provider.Paths, p => p.Contains("/manifests/000000000002-", StringComparison.Ordinal));
    }

    [Fact]
    public void Providers_without_safe_commits_are_rejected()
    {
        var provider = new FakeProvider(SyncProviderCapabilities.None);

        Assert.Throws<SyncConfigurationException>(() => new Device(provider));
    }

    [Fact]
    public void Read_only_sources_require_upload_only_mode()
    {
        var provider = new FakeProvider();

        Assert.Throws<SyncConfigurationException>(() => new Device(provider, source: new FakeSource("data", SyncDataSourceCapabilities.ReadOnly)));
        using var ok = new Device(provider, SyncMode.UploadOnly, source: new FakeSource("data", SyncDataSourceCapabilities.ReadOnly));
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        var options = new DataSyncOptions { ApplicationId = "bad/id", MaxParallelTransfers = 0 };

        IReadOnlyList<string> errors = options.Validate();

        Assert.Equal(2, errors.Count);
        Assert.Throws<SyncConfigurationException>(() => new DataSyncService([], new FakeStateStore(), Options.Create(options)));
    }
}
