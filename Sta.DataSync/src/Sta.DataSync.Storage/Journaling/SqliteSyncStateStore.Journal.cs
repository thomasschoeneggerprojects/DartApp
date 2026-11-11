using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Sta.DataSync.Storage.State;

/// <summary>Journal part of <see cref="SqliteSyncStateStore"/>: durable records of unfinished sessions for crash recovery.</summary>
public sealed partial class SqliteSyncStateStore
{
    /// <inheritdoc />
    public Task<SyncJournalEntry?> GetOpenJournalAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return RunAsync(async connection =>
        {
            await using SqliteCommand command = Command(connection, null,
                "SELECT session_id, phase, payload, updated_utc FROM journal WHERE scope_id = $s ORDER BY updated_utc DESC LIMIT 1",
                p => p.AddWithValue("$s", scopeId));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

            return new SyncJournalEntry(
                reader.GetString(0),
                scopeId,
                reader.GetString(1),
                NullableString(reader, 2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteJournalAsync(SyncJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RunAsync(connection => ExecuteAsync(connection, null, """
            INSERT INTO journal (session_id, scope_id, phase, payload, updated_utc) VALUES ($id, $s, $phase, $payload, $utc)
            ON CONFLICT (session_id) DO UPDATE SET phase = excluded.phase, payload = excluded.payload, updated_utc = excluded.updated_utc
            """,
            p =>
            {
                p.AddWithValue("$id", entry.SessionId);
                p.AddWithValue("$s", entry.ScopeId);
                p.AddWithValue("$phase", entry.Phase);
                p.AddWithValue("$payload", Db(entry.Payload));
                p.AddWithValue("$utc", Db(entry.UpdatedUtc));
            },
            cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteJournalAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return RunAsync(connection => ExecuteAsync(connection, null, "DELETE FROM journal WHERE session_id = $id",
            p => p.AddWithValue("$id", sessionId), cancellationToken), cancellationToken);
    }
}
