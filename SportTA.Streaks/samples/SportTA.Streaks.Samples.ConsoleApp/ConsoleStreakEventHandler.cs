using SportTA.Streaks;

namespace SportTA.Streaks.Samples.ConsoleApp;

/// <summary>Stands in for the app's UI layer: achievements, toasts, sounds.</summary>
internal sealed class ConsoleStreakEventHandler : IStreakEventHandler
{
    public Task HandleAsync(StreakEvent streakEvent, CancellationToken cancellationToken)
    {
        string text = streakEvent.Type switch
        {
            StreakEventType.MilestoneReached => $"Milestone {streakEvent.Milestone} reached{(streakEvent.IsFirstTimeMilestone ? " for the first time" : string.Empty)}!",
            StreakEventType.Broken => $"Streak of {streakEvent.PreviousCount} ended.",
            StreakEventType.Frozen => $"{streakEvent.FreezesConsumed} freeze(s) protected your streak.",
            _ => $"{streakEvent.Type}: {streakEvent.CurrentCount}",
        };
        Console.WriteLine($"    (event) {streakEvent.DefinitionId}: {text}");
        return Task.CompletedTask;
    }
}
