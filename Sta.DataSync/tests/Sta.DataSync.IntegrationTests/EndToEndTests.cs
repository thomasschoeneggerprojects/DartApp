using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Sta.DataSync.Testing;

namespace Sta.DataSync.IntegrationTests;

/// <summary>Full stack: DI, SQLite state, file and SQLite sources, Google Drive provider (in-memory Drive API).</summary>
public sealed class EndToEndTests
{
    [Fact]
    public async Task Files_synchronize_between_two_devices()
    {
        var drive = new FakeDriveServer();
        using var windows = new TestDevice(drive, "windows");
        using var android = new TestDevice(drive, "android");
        windows.Write("settings.json", "{\"theme\":\"dark\"}");
        windows.Write("achievements/first.json", "{\"won\":true}");
        windows.Write("debug.log", "excluded");

        SyncResult up = await windows.Sync.SynchronizeAsync("settings");
        SyncResult down = await android.Sync.SynchronizeAsync("settings");
        android.Write("settings.json", "{\"theme\":\"light\"}");
        await android.Sync.SynchronizeAsync("settings");
        await windows.Sync.SynchronizeAsync("settings");

        Assert.Equal(2, up.UploadedCount);
        Assert.Equal(2, down.DownloadedCount);
        Assert.Equal("{\"won\":true}", android.Read("achievements/first.json"));
        Assert.Null(android.Read("debug.log"));
        Assert.Equal("{\"theme\":\"light\"}", windows.Read("settings.json"));
        Assert.All(drive.Names, n => Assert.StartsWith("Sta.DataSync/DartTA/settings/", n, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deletions_and_conflicts_never_lose_data()
    {
        var drive = new FakeDriveServer();
        using var a = new TestDevice(drive, "a");
        using var b = new TestDevice(drive, "b");
        a.Write("notes.txt", "base");
        a.Write("old.txt", "remove me");
        await a.Sync.SynchronizeAsync("settings");
        await b.Sync.SynchronizeAsync("settings");

        File.Delete(a.File("old.txt"));
        a.Write("notes.txt", "from A");
        b.Write("notes.txt", "from B");
        await a.Sync.SynchronizeAsync("settings");
        await b.Sync.SynchronizeAsync("settings");
        await a.Sync.SynchronizeAsync("settings");

        Assert.Null(b.Read("old.txt"));
        string copy = Directory.GetFiles(a.Files, "notes (conflict*").Select(Path.GetFileName).Single()!;
        Assert.Equal(new[] { "from A", "from B" }.Order(), new[] { a.Read("notes.txt")!, a.Read(copy)! }.Order());
        Assert.Equal(a.Read("notes.txt"), b.Read("notes.txt"));
        Assert.Equal(a.Read(copy), b.Read(copy));
    }

    [Fact]
    public async Task Corrupt_or_failed_downloads_leave_local_files_intact()
    {
        var drive = new FakeDriveServer();
        using var a = new TestDevice(drive, "a");
        using var b = new TestDevice(drive, "b");
        a.Write("data.json", "v1");
        await a.Sync.SynchronizeAsync("settings");
        await b.Sync.SynchronizeAsync("settings");
        a.Write("data.json", "v2-with-more-bytes");
        await a.Sync.SynchronizeAsync("settings");

        drive.Intercept = request => request.RequestUri!.Query.Contains("alt=media", StringComparison.Ordinal) && !request.RequestUri.AbsolutePath.Contains("manifest", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("tampered content!!") }
            : null;
        SyncResult tampered = await b.Sync.SynchronizeAsync("settings");
        drive.Intercept = null;

        Assert.NotEmpty(tampered.Failures);
        Assert.Equal("v1", b.Read("data.json"));
        Assert.Empty(Directory.GetFiles(b.Files, "*.sta-tmp", SearchOption.AllDirectories));
        await b.Sync.SynchronizeAsync("settings");
        Assert.Equal("v2-with-more-bytes", b.Read("data.json"));
    }

    [Fact]
    public async Task Offline_changes_are_kept_and_uploaded_after_a_restart()
    {
        var drive = new FakeDriveServer();
        using var phone = new TestDevice(drive, "phone");
        using var pc = new TestDevice(drive, "pc");
        phone.Write("training.json", "offline session");
        drive.Intercept = _ => throw new HttpRequestException("No route to host");

        SyncResult offline = await phone.Sync.SynchronizeAsync("settings");
        phone.Restart();
        drive.Intercept = null;
        SyncResult online = await phone.Sync.SynchronizeAsync("settings");
        await pc.Sync.SynchronizeAsync("settings");

        Assert.Equal(SyncResultStatus.Failed, offline.Status);
        Assert.True(Assert.Single(offline.Failures).IsTransient);
        Assert.Equal("offline session", phone.Read("training.json"));
        Assert.Equal(1, online.UploadedCount);
        Assert.Equal("offline session", pc.Read("training.json"));
    }

    [Fact]
    public async Task Sqlite_database_snapshots_are_consistent_and_restore_safely()
    {
        var drive = new FakeDriveServer();
        using var a = new TestDevice(drive, "a");
        using var b = new TestDevice(drive, "b");
        Directory.CreateDirectory(a.Root);
        using (var writer = Open(a.DatabasePath))
        {
            Execute(writer, "PRAGMA journal_mode = WAL; CREATE TABLE matches (id INTEGER PRIMARY KEY, winner TEXT NOT NULL);");
            for (int i = 0; i < 100; i++) Execute(writer, $"INSERT INTO matches (winner) VALUES ('player{i}')");

            // Committed rows still in the WAL (not checkpointed) while the app keeps its connection open.
            SyncResult uploaded = await a.Sync.SynchronizeAsync("database");
            Assert.Equal(1, uploaded.UploadedCount);
        }

        Directory.CreateDirectory(b.Root);
        using var reader = Open(b.DatabasePath);
        Execute(reader, "CREATE TABLE placeholder (x INTEGER)");
        // B's database was created independently. A database source cannot hold conflict copies, so the conflict is
        // reported instead of resolved automatically, and nothing is overwritten until the application decides.
        SyncResult first = await b.Sync.SynchronizeAsync("database");
        SyncConflict conflict = Assert.Single(first.Conflicts.Concat(await b.Sync.GetConflictsAsync("database")).DistinctBy(c => c.ItemId));
        await b.Sync.ResolveConflictAsync("database", conflict.ItemId, ConflictResolution.KeepRemote);
        SyncResult restored = await b.Sync.SynchronizeAsync("database");

        Assert.Equal(1, restored.DownloadedCount);
        Assert.Equal(100L, Scalar(reader, "SELECT COUNT(*) FROM matches"));
        Assert.Equal("ok", Scalar(reader, "PRAGMA integrity_check"));
        SyncResult again = await a.Sync.SynchronizeAsync("database");
        Assert.Equal(SyncResultStatus.NoChanges, again.Status);
    }

    [Fact]
    public void Scopes_must_name_their_provider_when_several_are_registered()
    {
        var drive = new FakeDriveServer();
        using var device = new TestDevice(drive, "x");

        Assert.Throws<SyncConfigurationException>(() => device.Restart(services => services.AddSingleton<ISyncProvider>(new SecondProvider())));
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class SecondProvider : ISyncProvider
    {
        public string ProviderId => "second";

        public SyncProviderCapabilities Capabilities => SyncProviderCapabilities.ConditionalWrites;

        public Task<IReadOnlyList<RemoteSyncItem>> ListItemsAsync(string pathPrefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RemoteSyncItem?> GetItemAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Stream> DownloadAsync(RemoteSyncItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RemoteSyncItem> UploadAsync(RemoteUpload upload, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(RemoteSyncItem item, string? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
