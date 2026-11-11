namespace SportTA.Streaks.Tests.Infrastructure;

/// <summary>Throws a concurrency conflict for the first <c>failures</c> saves.</summary>
public sealed class ConflictingRepository(IStreakRepository inner, int failures) : IStreakRepository
{
    private int _remaining = failures;

    public Task<Streak?> GetAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
        => inner.GetAsync(playerId, definitionId, cancellationToken);

    public Task<IReadOnlyList<Streak>> GetAllAsync(Guid playerId, CancellationToken cancellationToken = default)
        => inner.GetAllAsync(playerId, cancellationToken);

    public Task SaveAsync(Streak streak, long expectedVersion, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Decrement(ref _remaining) >= 0)
        {
            throw new StreakConcurrencyException("simulated conflict");
        }

        return inner.SaveAsync(streak, expectedVersion, cancellationToken);
    }

    public Task<bool> DeleteAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
        => inner.DeleteAsync(playerId, definitionId, cancellationToken);
}

/// <summary>Fails every save with an I/O error.</summary>
public sealed class FailingRepository(IStreakRepository inner) : IStreakRepository
{
    public Task<Streak?> GetAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
        => inner.GetAsync(playerId, definitionId, cancellationToken);

    public Task<IReadOnlyList<Streak>> GetAllAsync(Guid playerId, CancellationToken cancellationToken = default)
        => inner.GetAllAsync(playerId, cancellationToken);

    public Task SaveAsync(Streak streak, long expectedVersion, CancellationToken cancellationToken = default)
        => throw new IOException("Simulated disk failure.");

    public Task<bool> DeleteAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
        => inner.DeleteAsync(playerId, definitionId, cancellationToken);
}

/// <summary>Blocks the first read until <see cref="Release"/> is called.</summary>
public sealed class BlockingRepository(IStreakRepository inner) : IStreakRepository
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reads;

    public Task FirstReadStarted => _firstReadStarted.Task;

    public void Release() => _gate.TrySetResult();

    public async Task<Streak?> GetAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _reads) == 1)
        {
            _firstReadStarted.TrySetResult();
            await _gate.Task;
        }

        return await inner.GetAsync(playerId, definitionId, cancellationToken);
    }

    public Task<IReadOnlyList<Streak>> GetAllAsync(Guid playerId, CancellationToken cancellationToken = default)
        => inner.GetAllAsync(playerId, cancellationToken);

    public Task SaveAsync(Streak streak, long expectedVersion, CancellationToken cancellationToken = default)
        => inner.SaveAsync(streak, expectedVersion, cancellationToken);

    public Task<bool> DeleteAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
        => inner.DeleteAsync(playerId, definitionId, cancellationToken);
}
