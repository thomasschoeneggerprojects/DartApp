using Microsoft.Data.Sqlite;

namespace Sta.DataSync.Storage.SQLite;

/// <summary>
/// Versioned schema of the state database. Each migration runs in a transaction and bumps <c>PRAGMA user_version</c>;
/// new versions only append migrations, so any older database upgrades step by step.
/// </summary>
internal static class SqliteSchema
{
    /// <summary>Migrations, index + 1 = resulting schema version.</summary>
    private static readonly string[][] Migrations =
    [
        // 1: initial schema.
        [
            "CREATE TABLE meta (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL)",
            """
            CREATE TABLE scopes (
                scope_id TEXT NOT NULL PRIMARY KEY,
                provider_id TEXT NOT NULL,
                manifest_revision INTEGER NOT NULL DEFAULT 0,
                manifest_path TEXT NULL,
                own_commit_path TEXT NULL,
                own_commit_rollback TEXT NULL,
                change_token TEXT NULL,
                last_success_utc TEXT NULL,
                last_attempt_utc TEXT NULL,
                last_error TEXT NULL)
            """,
            """
            CREATE TABLE items (
                scope_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                base_hash TEXT NULL,
                base_length INTEGER NOT NULL DEFAULT 0,
                base_version INTEGER NOT NULL DEFAULT 0,
                base_deleted INTEGER NOT NULL DEFAULT 0,
                local_fingerprint TEXT NULL,
                local_hash TEXT NULL,
                PRIMARY KEY (scope_id, item_id))
            """,
            """
            CREATE TABLE conflicts (
                scope_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                payload TEXT NOT NULL,
                resolution TEXT NULL,
                PRIMARY KEY (scope_id, item_id))
            """,
            """
            CREATE TABLE journal (
                session_id TEXT NOT NULL PRIMARY KEY,
                scope_id TEXT NOT NULL,
                phase TEXT NOT NULL,
                payload TEXT NULL,
                updated_utc TEXT NOT NULL)
            """,
            "CREATE INDEX ix_journal_scope ON journal (scope_id)",
        ],

        // 2: remote cleanup bookkeeping.
        [
            "ALTER TABLE scopes ADD COLUMN last_cleanup_utc TEXT NULL",
        ],
    ];

    /// <summary>Schema version this code writes.</summary>
    public static int CurrentVersion => Migrations.Length;

    /// <summary>Brings the database to <see cref="CurrentVersion"/>.</summary>
    /// <exception cref="SyncIntegrityException">The database was written by a newer version.</exception>
    public static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        int version = await GetVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version > CurrentVersion)
        {
            throw new SyncIntegrityException(
                $"The synchronization state database has schema version {version}; this version supports up to {CurrentVersion}.");
        }

        for (int target = version + 1; target <= CurrentVersion; target++)
        {
            await ApplyAsync(connection, target, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ApplyAsync(SqliteConnection connection, int target, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (string sql in Migrations[target - 1])
        {
            await ExecuteAsync(connection, transaction, sql, cancellationToken).ConfigureAwait(false);
        }

        // PRAGMA does not accept parameters; target is an integer from this class.
        await ExecuteAsync(
            connection,
            transaction,
            "PRAGMA user_version = " + target.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
