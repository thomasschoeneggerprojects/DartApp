using SportTA.Streaks;

namespace SportTA.Streaks.Samples.ConsoleApp;

/// <summary>Stand-in for an application's existing persistence (save game, SQLite repository, cloud sync, ...).</summary>
internal sealed class GameSaveStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Streak> _records = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    public T Read<T>(Func<Dictionary<string, Streak>, T> read)
    {
        lock (_gate)
        {
            return read(_records);
        }
    }

    public void Write(Action<Dictionary<string, Streak>> write)
    {
        lock (_gate)
        {
            write(_records);
        }
    }
}

/// <summary>Adapter that stores streaks inside the application's own persistence instead of separate files.</summary>
internal sealed class GameSaveStreakRepository(GameSaveStore store) : IStreakRepository
{
    private static string Key(Guid playerId, string definitionId) => $"{playerId:N}/{definitionId}";

    public Task<Streak?> GetAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
        => Task.FromResult(store.Read(r => r.GetValueOrDefault(Key(playerId, definitionId))));

    public Task<IReadOnlyList<Streak>> GetAllAsync(Guid playerId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Streak>>(store.Read(r => r.Values.Where(s => s.PlayerId == playerId).ToArray()));

    public Task SaveAsync(Streak streak, long expectedVersion, CancellationToken cancellationToken = default)
    {
        store.Write(records =>
        {
            string key = Key(streak.PlayerId, streak.DefinitionId);
            long stored = records.TryGetValue(key, out Streak? existing) ? existing.Version : 0;
            if (stored != expectedVersion)
            {
                throw new StreakConcurrencyException($"Streak '{key}' changed concurrently.");
            }

            records[key] = streak;
        });
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
    {
        bool removed = false;
        store.Write(records => removed = records.Remove(Key(playerId, definitionId)));
        return Task.FromResult(removed);
    }
}
