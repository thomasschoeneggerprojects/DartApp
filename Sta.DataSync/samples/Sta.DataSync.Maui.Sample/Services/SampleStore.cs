using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Sta.DataSync.Maui.Sample.Services;

/// <summary>
/// The sample app's own data: JSON notes and a SQLite database of training records. It works fully offline;
/// synchronization is an add-on that reads and writes through the registered data sources.
/// </summary>
public sealed class SampleStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SampleStore(string appDataDirectory)
    {
        NotesDirectory = Path.Combine(appDataDirectory, "notes");
        DatabasePath = Path.Combine(appDataDirectory, "records.db3");
        Directory.CreateDirectory(NotesDirectory);
    }

    public string NotesDirectory { get; }

    public string DatabasePath { get; }

    /// <summary>Raised after a restore replaced the database.</summary>
    public event EventHandler? Reloaded;

    public async Task SaveNoteAsync(string text, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(NotesDirectory, "note.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { text, savedUtc = DateTimeOffset.UtcNow }), cancellationToken);
    }

    public async Task<string> LoadNoteAsync(CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(NotesDirectory, "note.json");
        if (!File.Exists(path)) return string.Empty;
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        return document.RootElement.TryGetProperty("text", out JsonElement text) ? text.GetString() ?? string.Empty : string.Empty;
    }

    public async Task AddRecordAsync(string text, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO records (created_utc, text) VALUES ($c, $t)";
            command.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$t", text);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> CountRecordsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM records";
            return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called before a restore: wait for writers to finish and release pooled connections.</summary>
    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        SqliteConnection.ClearAllPools();
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Called after a restore.</summary>
    public Task ReloadAsync()
    {
        _gate.Release();
        Reloaded?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; CREATE TABLE IF NOT EXISTS records (id INTEGER PRIMARY KEY, created_utc TEXT NOT NULL, text TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
