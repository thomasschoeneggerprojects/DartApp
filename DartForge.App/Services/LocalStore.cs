using SQLite;
using System.Text.Json;
using DartForge.Core.Models;
namespace DartForge.App.Services;
public sealed class LocalStore
{
    private readonly SemaphoreSlim _mutex = new(1,1);
    private SQLiteAsyncConnection? _db;
    private async Task<SQLiteAsyncConnection> OpenAsync()
    {
        if (_db != null) return _db;
        var db = new SQLiteAsyncConnection(Path.Combine(FileSystem.AppDataDirectory, "dartforge.db3"));
        await db.CreateTableAsync<MatchRecord>();
        await db.CreateTableAsync<TrainingRecord>();
        _db = db; return db;
    }
    public async Task SaveMatchAsync(GameSnapshot snapshot)
    {
        await _mutex.WaitAsync();
        try
        {
            var db = await OpenAsync();
            await db.InsertAsync(new MatchRecord { PlayedUtc = DateTimeOffset.UtcNow.ToString("O"), Mode = snapshot.Settings.Kind.ToString(), Winner = snapshot.Players.FirstOrDefault(x=>x.Id == snapshot.WinnerId)?.Name ?? "Unfinished", Json = JsonSerializer.Serialize(snapshot) });
        }
        finally { _mutex.Release(); }
    }
    public async Task<List<MatchRecord>> MatchesAsync() => await (await OpenAsync()).Table<MatchRecord>().OrderByDescending(x=>x.Id).ToListAsync();
    public async Task SaveTrainingAsync(string mode, int score, int darts)
    {
        var db = await OpenAsync();
        await db.InsertAsync(new TrainingRecord { Mode = mode, Score = score, Darts = darts, PlayedUtc = DateTimeOffset.UtcNow.ToString("O") });
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
