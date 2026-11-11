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
        var db = await OpenAsync();
        var record = new TrainingRecord { Mode = mode, Score = score, Darts = darts, PlayedUtc = DateTimeOffset.UtcNow.ToString("O") };
        await db.InsertAsync(record);
        return record.Id;
    }
    public async Task<List<TrainingRecord>> TrainingAsync() => await (await OpenAsync()).Table<TrainingRecord>().OrderByDescending(x=>x.Id).ToListAsync();
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
