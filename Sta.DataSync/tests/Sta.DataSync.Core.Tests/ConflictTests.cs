using Sta.DataSync.Core.Tests.Fakes;

namespace Sta.DataSync.Core.Tests;

public sealed class ConflictTests
{
    private static async Task<(FakeProvider Provider, Device A, Device B)> DivergedAsync(
        ConflictResolutionStrategy strategy,
        IConflictResolver? resolver = null,
        DateTimeOffset? aModified = null,
        DateTimeOffset? bModified = null)
    {
        var provider = new FakeProvider();
        var a = new Device(provider, strategy: strategy, resolver: resolver);
        var b = new Device(provider, strategy: strategy, resolver: resolver);
        a.Source.Set("notes.txt", "base");
        await a.SyncAsync();
        await b.SyncAsync();
        a.Source.Set("notes.txt", "edited on A", aModified);
        b.Source.Set("notes.txt", "edited on B", bModified);
        await a.SyncAsync();
        return (provider, a, b);
    }

    [Fact]
    public async Task Concurrent_modification_keeps_both_versions_by_default()
    {
        (_, Device a, Device b) = await DivergedAsync(ConflictResolutionStrategy.KeepBoth);

        SyncResult result = await b.SyncAsync();
        await a.SyncAsync();

        Assert.Equal(SyncResultStatus.Succeeded, result.Status);
        Assert.Equal("edited on B", b.Source.Get("notes.txt"));
        string copy = Assert.Single(b.Source.Ids, id => id.Contains("(conflict", StringComparison.Ordinal));
        Assert.Equal("edited on A", b.Source.Get(copy));
        Assert.Equal("edited on B", a.Source.Get("notes.txt"));
        Assert.Equal("edited on A", a.Source.Get(copy));
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task Keep_local_and_keep_remote_strategies()
    {
        (_, Device a1, Device local) = await DivergedAsync(ConflictResolutionStrategy.KeepLocal);
        await local.SyncAsync();
        await a1.SyncAsync();
        Assert.Equal("edited on B", a1.Source.Get("notes.txt"));

        (_, Device a2, Device remote) = await DivergedAsync(ConflictResolutionStrategy.KeepRemote);
        await remote.SyncAsync();
        Assert.Equal("edited on A", remote.Source.Get("notes.txt"));
        Assert.DoesNotContain(remote.Source.Ids, id => id.Contains("(conflict", StringComparison.Ordinal));
        foreach (Device d in new[] { a1, local, a2, remote }) d.Dispose();
    }

    [Fact]
    public async Task Manual_conflicts_are_reported_until_the_application_resolves_them()
    {
        (_, Device a, Device b) = await DivergedAsync(ConflictResolutionStrategy.Manual);

        SyncResult first = await b.SyncAsync();
        SyncResult again = await b.SyncAsync();

        SyncConflict conflict = Assert.Single(first.Conflicts);
        Assert.Equal("notes.txt", conflict.ItemId);
        Assert.Equal(SyncResultStatus.PartiallySucceeded, first.Status);
        Assert.Single(again.Conflicts);
        Assert.Equal("edited on B", b.Source.Get("notes.txt"));
        Assert.Single(await b.Service.GetConflictsAsync());

        await b.Service.ResolveConflictAsync("data", "notes.txt", ConflictResolution.KeepRemote);
        SyncResult resolved = await b.SyncAsync();

        Assert.Empty(resolved.Conflicts);
        Assert.Equal("edited on A", b.Source.Get("notes.txt"));
        Assert.Empty(await b.Service.GetConflictsAsync());
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task A_decision_is_not_applied_when_a_version_changed_after_it_was_made()
    {
        (_, Device a, Device b) = await DivergedAsync(ConflictResolutionStrategy.Manual);
        await b.SyncAsync();
        await b.Service.ResolveConflictAsync("data", "notes.txt", ConflictResolution.KeepRemote);

        b.Source.Set("notes.txt", "edited on B again");
        SyncResult result = await b.SyncAsync();

        Assert.Single(result.Conflicts);
        Assert.Equal("edited on B again", b.Source.Get("notes.txt"));
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task Custom_resolver_decides()
    {
        var resolver = new RecordingResolver(ConflictResolution.KeepLocal);
        (_, Device a, Device b) = await DivergedAsync(ConflictResolutionStrategy.Custom, resolver);

        await b.SyncAsync();
        await a.SyncAsync();

        Assert.Single(resolver.Seen);
        Assert.Equal("edited on B", a.Source.Get("notes.txt"));
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task Latest_wins_uses_modification_times_only_when_both_are_known()
    {
        DateTimeOffset older = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        (_, Device a, Device b) = await DivergedAsync(ConflictResolutionStrategy.LatestWins, aModified: older.AddHours(2), bModified: older);

        await b.SyncAsync();

        Assert.Equal("edited on A", b.Source.Get("notes.txt"));
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task Delete_versus_modify_is_a_conflict_that_keeps_the_content()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        using var b = new Device(provider);
        a.Source.Set("notes.txt", "base");
        await a.SyncAsync();
        await b.SyncAsync();
        a.Source.Remove("notes.txt");
        await a.SyncAsync();
        b.Source.Set("notes.txt", "still needed");

        await b.SyncAsync();
        await a.SyncAsync();

        Assert.Equal("still needed", b.Source.Get("notes.txt"));
        Assert.Equal("still needed", a.Source.Get("notes.txt"));
    }

    [Fact]
    public async Task Keep_both_is_deferred_when_the_source_cannot_create_copies()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        using var b = new Device(provider, source: new FakeSource("data", SyncDataSourceCapabilities.Write | SyncDataSourceCapabilities.Delete));
        a.Source.Set("db", "from A");
        await a.SyncAsync();
        b.Source.Set("db", "from B");

        SyncResult result = await b.SyncAsync();
        await a.SyncAsync();

        Assert.Single(result.Conflicts);
        Assert.Equal("from B", b.Source.Get("db"));
        Assert.Equal("from A", a.Source.Get("db"));
    }

    [Fact]
    public async Task A_failed_conflict_copy_never_lets_the_original_be_overwritten()
    {
        (FakeProvider provider, Device a, Device b) = await DivergedAsync(ConflictResolutionStrategy.KeepBoth);
        provider.Fault = (op, path) => op == "download" && path.Contains("/objects/", StringComparison.Ordinal) ? new SyncProviderException("broken") : null;

        SyncResult result = await b.SyncAsync();
        provider.Fault = null;
        using var c = new Device(provider);
        await c.SyncAsync();

        Assert.NotEmpty(result.Failures);
        Assert.Equal("edited on A", c.Source.Get("notes.txt"));
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task Both_sides_making_the_same_change_is_not_a_conflict()
    {
        var provider = new FakeProvider();
        using var a = new Device(provider);
        using var b = new Device(provider);
        a.Source.Set("x.txt", "base");
        await a.SyncAsync();
        await b.SyncAsync();
        a.Source.Set("x.txt", "same");
        b.Source.Set("x.txt", "same");
        await a.SyncAsync();

        SyncResult result = await b.SyncAsync();

        Assert.Empty(result.Conflicts);
        Assert.Equal(0, result.TransferredBytes);
    }

    private sealed class RecordingResolver(ConflictResolution decision) : IConflictResolver
    {
        public List<SyncConflict> Seen { get; } = [];

        public Task<ConflictResolution> ResolveAsync(SyncConflict conflict, CancellationToken cancellationToken = default)
        {
            Seen.Add(conflict);
            return Task.FromResult(decision);
        }
    }
}
