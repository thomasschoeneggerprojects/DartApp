using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Sta.DataSync.Storage.State;

namespace Sta.DataSync.Storage.Tests;

public sealed class SqliteSyncStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sta-datasync-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "state.db3");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private SqliteSyncStateStore Open() => new(Options.Create(new SqliteSyncStateStoreOptions { DatabasePath = DatabasePath }));

    [Fact]
    public async Task State_survives_reopening()
    {
        DateTimeOffset now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        string deviceId;
        using (SqliteSyncStateStore store = Open())
        {
            deviceId = await store.GetDeviceIdAsync();
            await store.CommitAsync(new SyncStateCommit
            {
                ScopeId = "settings",
                Scope = new ScopeSyncState { ScopeId = "settings", ProviderId = "google-drive", ManifestRevision = 7, ChangeToken = "42", LastSuccessUtc = now },
                UpsertItems = [new ItemSyncState { ScopeId = "settings", ItemId = "a.json", BaseHash = Hash('a'), BaseLength = 3, BaseVersion = 2, LocalFingerprint = "3:1", LocalHash = Hash('a') }],
                UpsertConflicts = [Conflict("b.json", now)],
            });
            await store.SetConflictResolutionAsync("settings", "b.json", ConflictResolution.KeepRemote);
        }

        using SqliteSyncStateStore reopened = Open();
        ScopeSyncState? scope = await reopened.GetScopeAsync("settings");
        IReadOnlyDictionary<string, ItemSyncState> items = await reopened.GetItemsAsync("settings");
        IReadOnlyDictionary<string, ConflictResolution> resolutions = await reopened.GetConflictResolutionsAsync("settings");

        Assert.Equal(deviceId, await reopened.GetDeviceIdAsync());
        Assert.Equal(7, scope!.ManifestRevision);
        Assert.Equal("42", scope.ChangeToken);
        Assert.Equal(now, scope.LastSuccessUtc);
        Assert.Equal(2, items["a.json"].BaseVersion);
        Assert.Equal(ConflictResolutionKind.KeepRemote, resolutions["b.json"].Kind);
        Assert.Equal("b.json", Assert.Single(await reopened.GetConflictsAsync(null)).ItemId);
    }

    [Fact]
    public async Task A_failing_commit_is_rolled_back_completely()
    {
        using SqliteSyncStateStore store = Open();
        var commit = new SyncStateCommit
        {
            ScopeId = "s",
            Scope = new ScopeSyncState { ScopeId = "s", ProviderId = "p", ManifestRevision = 5 },
            UpsertItems =
            [
                new ItemSyncState { ScopeId = "s", ItemId = "ok.txt", BaseHash = Hash('a') },
                new ItemSyncState { ScopeId = "s", ItemId = null! },
            ],
        };

        await Assert.ThrowsAnyAsync<Exception>(() => store.CommitAsync(commit));

        Assert.Null(await store.GetScopeAsync("s"));
        Assert.Empty(await store.GetItemsAsync("s"));
    }

    [Fact]
    public async Task Journal_entries_survive_a_restart_until_completed()
    {
        var entry = new SyncJournalEntry("session1", "s", "Committing", "{\"x\":1}", DateTimeOffset.UtcNow);
        using (SqliteSyncStateStore store = Open()) await store.WriteJournalAsync(entry);

        using SqliteSyncStateStore reopened = Open();
        SyncJournalEntry? open = await reopened.GetOpenJournalAsync("s");
        Assert.Equal("Committing", open!.Phase);
        Assert.Equal("{\"x\":1}", open.Payload);

        await reopened.CommitAsync(new SyncStateCommit { ScopeId = "s", CompleteJournalSession = "session1" });
        Assert.Null(await reopened.GetOpenJournalAsync("s"));
    }

    [Fact]
    public async Task Version_1_databases_are_migrated_without_data_loss()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE meta (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE scopes (scope_id TEXT NOT NULL PRIMARY KEY, provider_id TEXT NOT NULL, manifest_revision INTEGER NOT NULL DEFAULT 0,
                    manifest_path TEXT NULL, own_commit_path TEXT NULL, own_commit_rollback TEXT NULL, change_token TEXT NULL,
                    last_success_utc TEXT NULL, last_attempt_utc TEXT NULL, last_error TEXT NULL);
                CREATE TABLE items (scope_id TEXT NOT NULL, item_id TEXT NOT NULL, base_hash TEXT NULL, base_length INTEGER NOT NULL DEFAULT 0,
                    base_version INTEGER NOT NULL DEFAULT 0, base_deleted INTEGER NOT NULL DEFAULT 0, local_fingerprint TEXT NULL, local_hash TEXT NULL,
                    PRIMARY KEY (scope_id, item_id));
                CREATE TABLE conflicts (scope_id TEXT NOT NULL, item_id TEXT NOT NULL, payload TEXT NOT NULL, resolution TEXT NULL, PRIMARY KEY (scope_id, item_id));
                CREATE TABLE journal (session_id TEXT NOT NULL PRIMARY KEY, scope_id TEXT NOT NULL, phase TEXT NOT NULL, payload TEXT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO meta VALUES ('device_id', 'legacy-device');
                INSERT INTO scopes (scope_id, provider_id, manifest_revision) VALUES ('s', 'google-drive', 3);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        using SqliteSyncStateStore store = Open();
        ScopeSyncState? scope = await store.GetScopeAsync("s");

        Assert.Equal("legacy-device", await store.GetDeviceIdAsync());
        Assert.Equal(3, scope!.ManifestRevision);
        Assert.Null(scope.LastCleanupUtc);
        await store.CommitAsync(new SyncStateCommit { ScopeId = "s", Scope = scope with { LastCleanupUtc = DateTimeOffset.UnixEpoch } });
        Assert.Equal(DateTimeOffset.UnixEpoch, (await store.GetScopeAsync("s"))!.LastCleanupUtc);
    }

    [Fact]
    public async Task Databases_from_a_newer_version_are_refused()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }

        using SqliteSyncStateStore store = Open();
        await Assert.ThrowsAsync<SyncIntegrityException>(() => store.GetDeviceIdAsync());
    }

    [Fact]
    public async Task A_corrupt_database_is_moved_aside_and_recreated()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(DatabasePath, "this is not a sqlite database, just garbage bytes that fill the header");

        using SqliteSyncStateStore store = Open();
        string deviceId = await store.GetDeviceIdAsync();
        await store.CommitAsync(new SyncStateCommit { ScopeId = "s", Scope = new ScopeSyncState { ScopeId = "s", ProviderId = "p" } });

        Assert.False(string.IsNullOrEmpty(deviceId));
        Assert.NotNull(await store.GetScopeAsync("s"));
        Assert.Single(Directory.GetFiles(_directory, "state.db3.corrupt-*"));
    }

    [Fact]
    public async Task Reset_removes_only_the_scope()
    {
        using SqliteSyncStateStore store = Open();
        foreach (string id in new[] { "a", "b" })
        {
            await store.CommitAsync(new SyncStateCommit
            {
                ScopeId = id,
                Scope = new ScopeSyncState { ScopeId = id, ProviderId = "p" },
                UpsertItems = [new ItemSyncState { ScopeId = id, ItemId = "x", BaseHash = Hash('a') }],
            });
        }

        await store.ResetScopeAsync("a");

        Assert.Null(await store.GetScopeAsync("a"));
        Assert.Empty(await store.GetItemsAsync("a"));
        Assert.Single(await store.GetItemsAsync("b"));
    }

    [Fact]
    public async Task Concurrent_access_is_serialized()
    {
        using SqliteSyncStateStore store = Open();

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => store.CommitAsync(new SyncStateCommit
        {
            ScopeId = "s",
            UpsertItems = [new ItemSyncState { ScopeId = "s", ItemId = $"item{i}", BaseHash = Hash('a') }],
        }))));

        Assert.Equal(50, (await store.GetItemsAsync("s")).Count);
    }

    private static string Hash(char c) => ContentHash.Prefix + new string(c, 64);

    private static SyncConflict Conflict(string itemId, DateTimeOffset now)
        => new("settings", itemId, new SyncConflictVersion(Hash('b'), 1, now, "dev1"), new SyncConflictVersion(Hash('c'), 2, now, "dev2"), Hash('a'), now);
}
