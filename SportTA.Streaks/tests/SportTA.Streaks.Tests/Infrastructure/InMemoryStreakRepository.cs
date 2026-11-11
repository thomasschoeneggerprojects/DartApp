namespace SportTA.Streaks.Tests.Infrastructure;

/// <summary>Thread-safe in-memory repository with optimistic concurrency, mirroring the repository contract.</summary>
public sealed class InMemoryStreakRepository : IStreakRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid, string), Streak> _items = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public int SaveCount { get; private set; }

    public Task<Streak?> GetAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_items.GetValueOrDefault((playerId, definitionId)));
        }
    }

    public Task<IReadOnlyList<Streak>> GetAllAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<Streak> result = _items.Values.Where(s => s.PlayerId == playerId).ToArray();
            return Task.FromResult(result);
        }
    }

    public Task SaveAsync(Streak streak, long expectedVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            long stored = _items.TryGetValue((streak.PlayerId, streak.DefinitionId), out Streak? existing) ? existing.Version : 0;
            if (stored != expectedVersion)
            {
                throw new StreakConcurrencyException("conflict");
            }

            _items[(streak.PlayerId, streak.DefinitionId)] = streak;
            SaveCount++;
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.Remove((playerId, definitionId)));
        }
    }
}
