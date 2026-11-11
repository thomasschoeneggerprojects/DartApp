namespace SportTA.Streaks;

/// <summary>
/// Persistence boundary. Replace the default JSON implementation to store streaks in the application's own
/// database, save system or sync backend.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe and must implement optimistic concurrency in
/// <see cref="SaveAsync"/>: the write succeeds only if the stored version equals <c>expectedVersion</c>
/// (0 = "not stored yet"); otherwise <see cref="StreakConcurrencyException"/> is thrown and nothing is written.
/// The service retries automatically on that exception.
/// </remarks>
public interface IStreakRepository
{
    /// <summary>Loads one streak, or <see langword="null"/> when none is stored.</summary>
    Task<Streak?> GetAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default);

    /// <summary>Loads every stored streak of a player.</summary>
    Task<IReadOnlyList<Streak>> GetAllAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="streak"/> if the stored version equals <paramref name="expectedVersion"/>.</summary>
    /// <exception cref="StreakConcurrencyException">The stored version differs.</exception>
    Task SaveAsync(Streak streak, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Deletes one streak. Returns <see langword="false"/> when nothing was stored.</summary>
    Task<bool> DeleteAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default);
}
