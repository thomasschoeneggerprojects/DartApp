namespace SportTA.Streaks;

/// <summary>
/// Optional observer for streak events (achievements, notifications, celebrations). Handlers run after the change
/// was persisted; exceptions are logged and never undo or block the change. The same events are also returned in
/// <see cref="StreakResult.Events"/>.
/// </summary>
public interface IStreakEventHandler
{
    /// <summary>Handles one event.</summary>
    Task HandleAsync(StreakEvent streakEvent, CancellationToken cancellationToken);
}
