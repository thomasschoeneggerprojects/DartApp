using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sta.DataSync.Storage.SQLite;

namespace Sta.DataSync.Storage.State;

/// <summary>Options of <see cref="SqliteSyncStateStore"/>.</summary>
public sealed class SqliteSyncStateStoreOptions
{
    /// <summary>Path of the state database. Must not be the application's own database.</summary>
    public string DatabasePath { get; set; } = string.Empty;
}

/// <summary>
/// <see cref="ISyncStateStore"/> on a dedicated SQLite database (WAL mode). All operations are serialized, every
/// <see cref="SyncStateCommit"/> is one transaction, the schema migrates on first use, and a corrupt database is moved
/// aside and recreated (the next synchronization then behaves like a first synchronization: equal content is adopted,
/// different content becomes a conflict, nothing is overwritten).
/// </summary>
public sealed partial class SqliteSyncStateStore : ISyncStateStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ScopeTables = ["scopes", "items", "conflicts", "journal"];
    private static readonly string[] DatabaseFileSuffixes = ["", "-wal", "-shm"];

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    /// <summary>Creates the store.</summary>
    public SqliteSyncStateStore(IOptions<SqliteSyncStateStoreOptions> options, ILogger<SqliteSyncStateStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        string path = options.Value.DatabasePath;
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("DatabasePath is required.", nameof(options));

        _databasePath = Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default)
        => RunAsync(async connection =>
        {
            string? existing = await ScalarAsync<string?>(connection, null, "SELECT value FROM meta WHERE key = 'device_id'", null, cancellationToken).ConfigureAwait(false);
            if (existing is not null) return existing;

            string id = Guid.NewGuid().ToString("N")[..16];
            await ExecuteAsync(connection, null, "INSERT INTO meta (key, value) VALUES ('device_id', $v)", p => p.AddWithValue("$v", id), cancellationToken).ConfigureAwait(false);
            return id;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<ScopeSyncState?> GetScopeAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return RunAsync(async connection =>
        {
            await using SqliteCommand command = Command(connection, null, """
                SELECT provider_id, manifest_revision, manifest_path, own_commit_path, own_commit_rollback, change_token,
                       last_success_utc, last_attempt_utc, last_error, last_cleanup_utc
                FROM scopes WHERE scope_id = $s
                """, p => p.AddWithValue("$s", scopeId));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

            return new ScopeSyncState
            {
                ScopeId = scopeId,
                ProviderId = reader.GetString(0),
                ManifestRevision = reader.GetInt64(1),
                ManifestPath = NullableString(reader, 2),
                OwnCommitPath = NullableString(reader, 3),
                OwnCommitRollback = NullableString(reader, 4),
                ChangeToken = NullableString(reader, 5),
                LastSuccessUtc = NullableTime(reader, 6),
                LastAttemptUtc = NullableTime(reader, 7),
                LastError = NullableString(reader, 8),
                LastCleanupUtc = NullableTime(reader, 9),
            };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, ItemSyncState>> GetItemsAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return RunAsync<IReadOnlyDictionary<string, ItemSyncState>>(async connection =>
        {
            var items = new Dictionary<string, ItemSyncState>(StringComparer.Ordinal);
            await using SqliteCommand command = Command(connection, null, """
                SELECT item_id, base_hash, base_length, base_version, base_deleted, local_fingerprint, local_hash
                FROM items WHERE scope_id = $s
                """, p => p.AddWithValue("$s", scopeId));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var item = new ItemSyncState
                {
                    ScopeId = scopeId,
                    ItemId = reader.GetString(0),
                    BaseHash = NullableString(reader, 1),
                    BaseLength = reader.GetInt64(2),
                    BaseVersion = reader.GetInt64(3),
                    BaseDeleted = reader.GetInt64(4) != 0,
                    LocalFingerprint = NullableString(reader, 5),
                    LocalHash = NullableString(reader, 6),
                };
                items[item.ItemId] = item;
            }

            return items;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(string? scopeId, CancellationToken cancellationToken = default)
        => RunAsync<IReadOnlyList<SyncConflict>>(async connection =>
        {
            var conflicts = new List<SyncConflict>();
            await using SqliteCommand command = Command(
                connection,
                null,
                scopeId is null ? "SELECT payload FROM conflicts ORDER BY scope_id, item_id" : "SELECT payload FROM conflicts WHERE scope_id = $s ORDER BY item_id",
                p => p.AddWithValue("$s", (object?)scopeId ?? DBNull.Value));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                SyncConflict? conflict = JsonSerializer.Deserialize<SyncConflict>(reader.GetString(0), JsonOptions);
                if (conflict is not null) conflicts.Add(conflict);
            }

            return conflicts;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, ConflictResolution>> GetConflictResolutionsAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return RunAsync<IReadOnlyDictionary<string, ConflictResolution>>(async connection =>
        {
            var resolutions = new Dictionary<string, ConflictResolution>(StringComparer.Ordinal);
            await using SqliteCommand command = Command(connection, null,
                "SELECT item_id, resolution FROM conflicts WHERE scope_id = $s AND resolution IS NOT NULL",
                p => p.AddWithValue("$s", scopeId));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ConflictResolution? resolution = JsonSerializer.Deserialize<ConflictResolution>(reader.GetString(1), JsonOptions);
                if (resolution is not null) resolutions[reader.GetString(0)] = resolution;
            }

            return resolutions;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetConflictResolutionAsync(string scopeId, string itemId, ConflictResolution resolution, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(resolution);
        return RunAsync(async connection =>
        {
            int updated = await ExecuteAsync(connection, null,
                "UPDATE conflicts SET resolution = $r WHERE scope_id = $s AND item_id = $i",
                p =>
                {
                    p.AddWithValue("$r", JsonSerializer.Serialize(resolution, JsonOptions));
                    p.AddWithValue("$s", scopeId);
                    p.AddWithValue("$i", itemId);
                },
                cancellationToken).ConfigureAwait(false);
            if (updated == 0) throw new SyncItemNotFoundException($"No conflict is stored for '{itemId}' in scope '{scopeId}'.");
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task CommitAsync(SyncStateCommit commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return RunAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (commit.Scope is not null) await UpsertScopeAsync(connection, transaction, commit.Scope, cancellationToken).ConfigureAwait(false);
            foreach (ItemSyncState item in commit.UpsertItems) await UpsertItemAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
            foreach (string itemId in commit.RemoveItems) await DeleteRowAsync(connection, transaction, "items", commit.ScopeId, itemId, cancellationToken).ConfigureAwait(false);
            foreach (SyncConflict conflict in commit.UpsertConflicts) await UpsertConflictAsync(connection, transaction, conflict, cancellationToken).ConfigureAwait(false);
            foreach (string itemId in commit.RemoveConflicts) await DeleteRowAsync(connection, transaction, "conflicts", commit.ScopeId, itemId, cancellationToken).ConfigureAwait(false);
            if (commit.CompleteJournalSession is not null)
            {
                await ExecuteAsync(connection, transaction, "DELETE FROM journal WHERE session_id = $id",
                    p => p.AddWithValue("$id", commit.CompleteJournalSession), cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ResetScopeAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return RunAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (string table in ScopeTables)
            {
                await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE scope_id = $s",
                    p => p.AddWithValue("$s", scopeId), cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>Releases the store; pooled connections to the file are closed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private static Task<int> UpsertScopeAsync(SqliteConnection connection, SqliteTransaction transaction, ScopeSyncState scope, CancellationToken cancellationToken)
        => ExecuteAsync(connection, transaction, """
            INSERT INTO scopes (scope_id, provider_id, manifest_revision, manifest_path, own_commit_path, own_commit_rollback,
                                change_token, last_success_utc, last_attempt_utc, last_error, last_cleanup_utc)
            VALUES ($s, $p, $rev, $path, $own, $rollback, $token, $success, $attempt, $error, $cleanup)
            ON CONFLICT (scope_id) DO UPDATE SET
                provider_id = excluded.provider_id, manifest_revision = excluded.manifest_revision,
                manifest_path = excluded.manifest_path, own_commit_path = excluded.own_commit_path,
                own_commit_rollback = excluded.own_commit_rollback, change_token = excluded.change_token,
                last_success_utc = excluded.last_success_utc, last_attempt_utc = excluded.last_attempt_utc,
                last_error = excluded.last_error, last_cleanup_utc = excluded.last_cleanup_utc
            """,
            p =>
            {
                p.AddWithValue("$s", scope.ScopeId);
                p.AddWithValue("$p", scope.ProviderId);
                p.AddWithValue("$rev", scope.ManifestRevision);
                p.AddWithValue("$path", Db(scope.ManifestPath));
                p.AddWithValue("$own", Db(scope.OwnCommitPath));
                p.AddWithValue("$rollback", Db(scope.OwnCommitRollback));
                p.AddWithValue("$token", Db(scope.ChangeToken));
                p.AddWithValue("$success", Db(scope.LastSuccessUtc));
                p.AddWithValue("$attempt", Db(scope.LastAttemptUtc));
                p.AddWithValue("$error", Db(scope.LastError));
                p.AddWithValue("$cleanup", Db(scope.LastCleanupUtc));
            },
            cancellationToken);

    private static Task<int> UpsertItemAsync(SqliteConnection connection, SqliteTransaction transaction, ItemSyncState item, CancellationToken cancellationToken)
        => ExecuteAsync(connection, transaction, """
            INSERT INTO items (scope_id, item_id, base_hash, base_length, base_version, base_deleted, local_fingerprint, local_hash)
            VALUES ($s, $i, $hash, $len, $ver, $del, $fp, $lh)
            ON CONFLICT (scope_id, item_id) DO UPDATE SET
                base_hash = excluded.base_hash, base_length = excluded.base_length, base_version = excluded.base_version,
                base_deleted = excluded.base_deleted, local_fingerprint = excluded.local_fingerprint, local_hash = excluded.local_hash
            """,
            p =>
            {
                p.AddWithValue("$s", item.ScopeId);
                p.AddWithValue("$i", item.ItemId);
                p.AddWithValue("$hash", Db(item.BaseHash));
                p.AddWithValue("$len", item.BaseLength);
                p.AddWithValue("$ver", item.BaseVersion);
                p.AddWithValue("$del", item.BaseDeleted ? 1 : 0);
                p.AddWithValue("$fp", Db(item.LocalFingerprint));
                p.AddWithValue("$lh", Db(item.LocalHash));
            },
            cancellationToken);

    private static Task<int> UpsertConflictAsync(SqliteConnection connection, SqliteTransaction transaction, SyncConflict conflict, CancellationToken cancellationToken)
        => ExecuteAsync(connection, transaction, """
            INSERT INTO conflicts (scope_id, item_id, payload, resolution) VALUES ($s, $i, $payload, NULL)
            ON CONFLICT (scope_id, item_id) DO UPDATE SET payload = excluded.payload, resolution = NULL
            """,
            p =>
            {
                p.AddWithValue("$s", conflict.ScopeId);
                p.AddWithValue("$i", conflict.ItemId);
                p.AddWithValue("$payload", JsonSerializer.Serialize(conflict, JsonOptions));
            },
            cancellationToken);

    private static Task<int> DeleteRowAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string scopeId, string itemId, CancellationToken cancellationToken)
        => ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE scope_id = $s AND item_id = $i",
            p =>
            {
                p.AddWithValue("$s", scopeId);
                p.AddWithValue("$i", itemId);
            },
            cancellationToken);

    private async Task<T> RunAsync<T>(Func<SqliteConnection, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                await InitializeAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }

            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await operation(connection).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        try
        {
            await PrepareDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            await QuarantineAsync(ex.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (CorruptStateException ex)
        {
            await QuarantineAsync(ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task QuarantineAsync(string reason, CancellationToken cancellationToken)
    {
        string target = $"{_databasePath}.corrupt-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        LogCorruptState(_logger, _databasePath, target, reason);
        SqliteConnection.ClearAllPools();
        foreach (string suffix in DatabaseFileSuffixes)
        {
            if (File.Exists(_databasePath + suffix)) File.Move(_databasePath + suffix, target + suffix, overwrite: true);
        }

        await PrepareDatabaseAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PrepareDatabaseAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string? check = await ScalarAsync<string?>(connection, null, "PRAGMA quick_check", null, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase)) throw new CorruptStateException(check ?? "quick_check failed");

        await ScalarAsync<string?>(connection, null, "PRAGMA journal_mode = WAL", null, cancellationToken).ConfigureAwait(false);
        int before = await SqliteSchema.GetVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        await SqliteSchema.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
        if (before != SqliteSchema.CurrentVersion) LogMigrated(_logger, before, SqliteSchema.CurrentVersion);
    }

    private static bool IsCorruption(SqliteException ex)
        => ex.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */;

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, Action<SqliteParameterCollection>? parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        parameters?.Invoke(command.Parameters);
        return command;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, Action<SqliteParameterCollection>? parameters, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql, Action<SqliteParameterCollection>? parameters, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(connection, transaction, sql, parameters);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)value;
    }

    private static object Db(string? value) => (object?)value ?? DBNull.Value;

    private static object Db(DateTimeOffset? value)
        => value is { } v ? v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value;

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableTime(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Synchronization state database {Path} is corrupt ({Reason}); moved to {Target} and recreated. The next synchronization compares both sides from scratch.")]
    private static partial void LogCorruptState(ILogger logger, string path, string target, string reason);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Synchronization state database migrated from schema {From} to {To}.")]
    private static partial void LogMigrated(ILogger logger, int from, int to);

    private sealed class CorruptStateException(string message) : Exception(message);
}
