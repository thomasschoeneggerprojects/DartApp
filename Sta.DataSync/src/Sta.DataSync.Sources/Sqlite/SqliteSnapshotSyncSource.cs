using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Sta.DataSync.Sources.Sqlite;

/// <summary>Options of <see cref="SqliteSnapshotSyncSource"/>.</summary>
public sealed class SqliteSnapshotSyncSourceOptions
{
    /// <summary>Source id (and default scope id).</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Absolute path of the application's SQLite database.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>Item id of the snapshot; defaults to the database file name.</summary>
    public string? ItemId { get; set; }

    /// <summary>Directory for temporary snapshot and download files; defaults to the system temp directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Called before a downloaded snapshot is restored into the database, e.g. to pause writers or close cached
    /// connections. Optional: the restore uses SQLite's backup API and is safe while other connections are open.
    /// </summary>
    public Func<CancellationToken, Task>? BeforeRestoreAsync { get; set; }

    /// <summary>
    /// Called after a restore attempt, e.g. to reload caches or reopen connections. Always called when
    /// <see cref="BeforeRestoreAsync"/> completed, also when the restore failed.
    /// </summary>
    public Func<CancellationToken, Task>? AfterRestoreAsync { get; set; }

    /// <summary>Returns all validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!SyncPath.IsValid(SourceId) || SourceId.Contains('/', StringComparison.Ordinal)) errors.Add("SourceId must be a single valid path segment.");
        if (string.IsNullOrWhiteSpace(DatabasePath) || !Path.IsPathFullyQualified(DatabasePath)) errors.Add("DatabasePath must be an absolute path.");
        if (ItemId is not null && !SyncPath.IsValid(ItemId)) errors.Add("ItemId is not a valid sync path.");
        return errors;
    }
}

/// <summary>
/// Synchronizes a whole SQLite database as one snapshot item (version 1 works at file level, not record level).
/// <list type="bullet">
/// <item>Snapshots are created with SQLite's online backup API, which copies a transactionally consistent state
/// (including committed WAL content) while the application keeps using the database. The live file is never copied.</item>
/// <item>The snapshot is created lazily: an unchanged database (same file and WAL fingerprint) is not copied at all.</item>
/// <item>Every snapshot and every download passes <c>PRAGMA integrity_check</c> before it is used.</item>
/// <item>Restores also use the backup API (into the live database), so other connections see either the old or the new
/// state. A busy database makes the restore fail transiently; it is retried later.</item>
/// </list>
/// Deleting the database through synchronization is not supported (the source has no delete capability).
/// </summary>
public sealed class SqliteSnapshotSyncSource : ISyncDataSource, ISyncSnapshotSource
{
    private readonly SqliteSnapshotSyncSourceOptions _options;
    private readonly string _itemId;
    private readonly string _workingDirectory;

    /// <summary>Creates the source.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public SqliteSnapshotSyncSource(SqliteSnapshotSyncSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> errors = options.Validate();
        if (errors.Count > 0) throw new ArgumentException("Invalid SQLite source options: " + string.Join(" ", errors), nameof(options));

        _options = options;
        _itemId = options.ItemId ?? Path.GetFileName(options.DatabasePath);
        _workingDirectory = options.WorkingDirectory ?? Path.Combine(Path.GetTempPath(), "Sta.DataSync", "sqlite");
        SourceId = options.SourceId;
    }

    /// <inheritdoc />
    public string SourceId { get; }

    /// <inheritdoc />
    public SyncDataSourceCapabilities Capabilities => SyncDataSourceCapabilities.Write;

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncItem>> GetItemsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SyncItem>>(CurrentItem() is { } item ? [item] : []);

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(string itemId, CancellationToken cancellationToken = default)
    {
        EnsureItem(itemId);
        string snapshot = await CreateBackupAsync(cancellationToken).ConfigureAwait(false);
        return OpenTemporary(snapshot);
    }

    /// <inheritdoc />
    public Task<ISyncSnapshot> CreateSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<ISyncSnapshot>(new Snapshot(this, CurrentItem()));

    /// <inheritdoc />
    public async Task ApplyChangeAsync(SyncChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        EnsureItem(change.ItemId);
        if (change.Kind == SyncChangeKind.Delete) throw new SyncConfigurationException("Deleting the database through synchronization is not supported.");
        if (change.Content is null) throw new ArgumentException("A write needs content.", nameof(change));

        string downloaded = TemporaryPath("restore");
        try
        {
            await WriteFileAsync(downloaded, change.Content, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => VerifyIntegrity(downloaded), cancellationToken).ConfigureAwait(false);
            EnsureExpected(change);

            if (_options.BeforeRestoreAsync is { } before) await before(cancellationToken).ConfigureAwait(false);
            try
            {
                // Not cancelable once started: the backup API either completes or leaves the database unchanged.
                await Task.Run(() => Restore(downloaded), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                // Always paired with BeforeRestoreAsync, so an application lock taken there is released even on failure.
                if (_options.AfterRestoreAsync is { } after) await after(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            DeleteQuietly(downloaded);
        }
    }

    /// <summary>Fingerprint of database and WAL files (length and write time), without opening the database.</summary>
    private string? Fingerprint()
    {
        var db = new FileInfo(_options.DatabasePath);
        if (!db.Exists) return null;
        var wal = new FileInfo(_options.DatabasePath + "-wal");
        return string.Join(
            ':',
            db.Length.ToString(CultureInfo.InvariantCulture),
            db.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            wal.Exists ? wal.Length.ToString(CultureInfo.InvariantCulture) : "0",
            wal.Exists ? wal.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "0");
    }

    private SyncItem? CurrentItem()
    {
        var db = new FileInfo(_options.DatabasePath);
        return db.Exists ? new SyncItem(_itemId, db.Length, new DateTimeOffset(db.LastWriteTimeUtc, TimeSpan.Zero), Fingerprint()) : null;
    }

    private Task<string> CreateBackupAsync(CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            if (!File.Exists(_options.DatabasePath)) throw new SyncItemNotFoundException($"Database '{_itemId}' does not exist.");
            string target = TemporaryPath("snapshot");
            try
            {
                // Read-write (not create): read-only connections to WAL databases fail when the -shm file is missing.
                // The backup API only reads from the source.
                using (var source = Open(_options.DatabasePath, SqliteOpenMode.ReadWrite))
                using (var destination = Open(target, SqliteOpenMode.ReadWriteCreate))
                {
                    source.BackupDatabase(destination);
                    Execute(destination, "PRAGMA journal_mode = DELETE");
                }

                VerifyIntegrity(target);
                return target;
            }
            catch (SqliteException ex)
            {
                DeleteQuietly(target);
                throw Map(ex, "Creating a database snapshot failed.");
            }
        }, cancellationToken);

    private void Restore(string downloaded)
    {
        try
        {
            using var source = Open(downloaded, SqliteOpenMode.ReadOnly);
            using var destination = Open(_options.DatabasePath, SqliteOpenMode.ReadWriteCreate);
            Execute(destination, "PRAGMA busy_timeout = 5000");
            source.BackupDatabase(destination);
        }
        catch (SqliteException ex)
        {
            throw Map(ex, "Restoring the database snapshot failed; the database was not changed.");
        }
    }

    private static void VerifyIntegrity(string path)
    {
        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadOnly);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            string? result = command.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncIntegrityException($"The database snapshot failed its integrity check: {result}");
            }
        }
        catch (SqliteException ex)
        {
            throw new SyncIntegrityException("The database snapshot is not a valid SQLite database.", ex);
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SyncException Map(SqliteException ex, string message)
        => ex.SqliteErrorCode is 5 /* BUSY */ or 6 /* LOCKED */
            ? new SyncTransientException(message + " The database is busy.", ex)
            : new SyncIntegrityException(message, ex);

    private void EnsureExpected(SyncChange change)
    {
        string? current = Fingerprint();
        if (change.ExpectNotExists && current is not null) throw new SyncConcurrencyException($"Database '{_itemId}' was created during synchronization.");
        if (change.ExpectedCurrentFingerprint is { } expected && current != expected)
        {
            throw new SyncConcurrencyException($"Database '{_itemId}' was modified during synchronization; it is synchronized next time.");
        }
    }

    private void EnsureItem(string itemId)
    {
        if (itemId != _itemId) throw new SyncItemNotFoundException($"'{itemId}' is not part of SQLite source '{SourceId}'.");
    }

    private string TemporaryPath(string kind)
    {
        Directory.CreateDirectory(_workingDirectory);
        return Path.Combine(_workingDirectory, $"{SourceId}-{kind}-{Guid.NewGuid():N}.db");
    }

    private static Stream OpenTemporary(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);

    private static async Task WriteFileAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Temporary file in use; the OS cleans the temp directory.
        }
    }

    /// <summary>Session view: the item's fingerprint is fixed when the session starts; the backup is made on first read and reused.</summary>
    private sealed class Snapshot(SqliteSnapshotSyncSource owner, SyncItem? item) : ISyncSnapshot
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private string? _file;

        public IReadOnlyList<SyncItem> Items { get; } = item is null ? [] : [item];

        public async Task<Stream> OpenReadAsync(string itemId, CancellationToken cancellationToken = default)
        {
            owner.EnsureItem(itemId);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _file ??= await owner.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
                return new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            finally
            {
                _gate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_file is not null) DeleteQuietly(_file);
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
