namespace SportTA.Streaks;

/// <summary>
/// Application-facing streak API. All operations are asynchronous, thread-safe and deterministic.
/// The library never runs background work; the application decides when to evaluate
/// (startup, after an activity, when a statistics screen opens, after synchronization, ...).
/// </summary>
public interface IStreakService
{
    /// <summary>
    /// Records a successful activity with a random identifier. Not idempotent: prefer the
    /// <see cref="RecordActivityAsync(Guid, string, StreakActivity, CancellationToken)"/> overload for retries and synchronization.
    /// </summary>
    Task<StreakResult> RecordActivityAsync(Guid playerId, string streakId, DateTimeOffset timestamp, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an activity for one streak. Processing is idempotent per <see cref="StreakActivity.Id"/>.
    /// Missed periods before the activity are settled; periods after it are settled by <see cref="EvaluateAsync"/>,
    /// so pending (offline) activities should be recorded oldest first, before evaluating.
    /// </summary>
    Task<StreakResult> RecordActivityAsync(Guid playerId, string streakId, StreakActivity activity, CancellationToken cancellationToken = default);

    /// <summary>Records one activity for every registered streak, in registration order. Each rule decides whether it applies.</summary>
    Task<IReadOnlyList<StreakResult>> RecordActivityForAllAsync(Guid playerId, StreakActivity activity, CancellationToken cancellationToken = default);

    /// <summary>Returns the progress of a streak as of now without persisting anything.</summary>
    Task<StreakProgress> GetProgressAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default);

    /// <summary>Returns the progress of every registered streak for a player, in registration order.</summary>
    Task<IReadOnlyList<StreakProgress>> GetAllAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>Returns lifetime statistics of a streak as of now without persisting anything.</summary>
    Task<StreakStatistics> GetStatisticsAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default);

    /// <summary>Settles all periods that ended before now (consuming grace periods and freezes, or breaking the streak) and persists the result.</summary>
    Task<StreakResult> EvaluateAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default);

    /// <summary>Evaluates every registered streak for a player.</summary>
    Task<IReadOnlyList<StreakResult>> EvaluateAllAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants freezes (capped at <see cref="StreakDefinition.MaxFreezes"/>). Elapsed periods are settled first,
    /// so freezes protect future misses only; use <see cref="RestoreAsync"/> for an already broken streak.
    /// </summary>
    Task<StreakResult> AddFreezesAsync(Guid playerId, string streakId, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the most recently broken run. If a new run has started since, both runs are merged.
    /// The application decides whether restoring costs something.
    /// </summary>
    Task<StreakResult> RestoreAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default);

    /// <summary>Ends the current run. Longest count, totals, all-time milestones, freezes and the idempotency window are kept.</summary>
    Task<StreakResult> ResetAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default);

    /// <summary>Sets (or removes, when <paramref name="value"/> is <see langword="null"/>) an application-defined metadata entry.</summary>
    Task<StreakResult> SetMetadataAsync(Guid playerId, string streakId, string key, string? value, CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes the stored streak, including all history.</summary>
    Task<bool> DeleteAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default);
}
