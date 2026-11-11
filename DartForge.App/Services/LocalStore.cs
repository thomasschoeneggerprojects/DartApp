using SQLite;
using System.Text.Json;
using DartForge.Core.Models;
namespace DartForge.App.Services;
public sealed class LocalStore
{
    /// <summary>SQLite file with match and training history.</summary>
    public const string DatabaseFileName = "dartforge.db3";
    /// <summary>Directory that holds <see cref="DatabaseFileName"/>; other local data (e.g. streaks) is stored next to it.</summary>
    public static string DataDirectory => FileSystem.AppDataDirectory;
    private readonly SemaphoreSlim _mutex = new(1,1);
    private SQLiteAsyncConnection? _db;
    private async Task<SQLiteAsyncConnection> OpenAsync()
    {
        if (_db != null) return _db;
        var db = new SQLiteAsyncConnection(Path.Combine(DataDirectory, DatabaseFileName));
        await db.CreateTableAsync<MatchRecord>();
        await db.CreateTableAsync<TrainingRecord>();
        _db = db; return db;
    }
    /// <summary>Stores a match and returns its record id.</summary>
    public async Task<int> SaveMatchAsync(GameSnapshot snapshot)
    {
        await _mutex.WaitAsync();
        try
        {
            var db = await OpenAsync();
            var record = new MatchRecord { PlayedUtc = DateTimeOffset.UtcNow.ToString("O"), Mode = snapshot.Settings.Kind.ToString(), Winner = snapshot.Players.FirstOrDefault(x=>x.Id == snapshot.WinnerId)?.Name ?? "Unfinished", Json = JsonSerializer.Serialize(snapshot) };
            await db.InsertAsync(record);
            return record.Id;
        }
        finally { _mutex.Release(); }
    }
    public async Task<List<MatchRecord>> MatchesAsync() => await (await OpenAsync()).Table<MatchRecord>().OrderByDescending(x=>x.Id).ToListAsync();
    /// <summary>Stores a training session and returns its record id.</summary>
    public async Task<int> SaveTrainingAsync(string mode, int score, int darts)
    {
        await _mutex.WaitAsync();
        try
        {
            var db = await OpenAsync();
            var record = new TrainingRecord { Mode = mode, Score = score, Darts = darts, PlayedUtc = DateTimeOffset.UtcNow.ToString("O") };
            await db.InsertAsync(record);
            return record.Id;
        }
        finally { _mutex.Release(); }
    }
    public async Task<List<TrainingRecord>> TrainingAsync() => await (await OpenAsync()).Table<TrainingRecord>().OrderByDescending(x=>x.Id).ToListAsync();

    // ---- Cloud sync (Sta.DataSync restores the whole database through SQLite's backup API) ----

    /// <summary>Copy of the history taken right before a cloud version replaced it (one generation).</summary>
    public const string PreRestoreBackupFileName = "dartforge.before-sync.db3";

    /// <summary>Raised after the database was replaced by a version from the cloud; screens reload their data.</summary>
    public event EventHandler? DataReplaced;

    /// <summary>
    /// Called before a cloud version is restored: blocks new writes until <see cref="EndRestoreAsync"/> and keeps a
    /// backup of the current history, so choosing the cloud version can be undone by support if needed.
    /// </summary>
    public async Task BeginRestoreAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            string backup = Path.Combine(DataDirectory, PreRestoreBackupFileName);
            if (File.Exists(backup)) File.Delete(backup);
            await (await OpenAsync()).BackupAsync(backup);
        }
        catch
        {
            _mutex.Release();
            throw;
        }
    }

    /// <summary>Called after the restore attempt (also when it failed): allows writes again and tells screens to reload.</summary>
    public Task EndRestoreAsync()
    {
        _mutex.Release();
        DataReplaced?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
public sealed class MatchRecord
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public string Mode { get; set; } = "";
    public string Winner { get; set; } = "";
    public string PlayedUtc { get; set; } = "";
    public string Json { get; set; } = "";
}
public sealed class TrainingRecord
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public string Mode { get; set; } = "";
    public int Score { get; set; }
    public int Darts { get; set; }
    public string PlayedUtc { get; set; } = "";
}
