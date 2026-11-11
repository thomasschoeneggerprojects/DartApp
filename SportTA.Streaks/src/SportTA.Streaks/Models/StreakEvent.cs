namespace SportTA.Streaks;

/// <summary>A notification about a streak change. Emitted only after the change was persisted.</summary>
public sealed record StreakEvent
{
    /// <summary>Kind of event.</summary>
    public required StreakEventType Type { get; init; }

    /// <summary>The player.</summary>
    public required Guid PlayerId { get; init; }

    /// <summary>The streak definition.</summary>
    public required string DefinitionId { get; init; }

    /// <summary>The streak record.</summary>
    public required Guid StreakId { get; init; }

    /// <summary>Current count after the change.</summary>
    public required int CurrentCount { get; init; }

    /// <summary>Count before the change.</summary>
    public int PreviousCount { get; init; }

    /// <summary>The milestone for <see cref="StreakEventType.MilestoneReached"/>.</summary>
    public int? Milestone { get; init; }

    /// <summary><see langword="true"/> when the milestone was never reached before (useful for one-time rewards).</summary>
    public bool IsFirstTimeMilestone { get; init; }

    /// <summary>Freezes consumed for <see cref="StreakEventType.Frozen"/>.</summary>
    public int FreezesConsumed { get; init; }

    /// <summary>The activity that caused the event, if any.</summary>
    public string? ActivityId { get; init; }

    /// <summary>When the change was processed (UTC).</summary>
    public required DateTimeOffset OccurredAt { get; init; }
}
