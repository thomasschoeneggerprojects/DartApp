namespace SportTA.Streaks;

/// <summary>Lifetime statistics of a streak, evaluated against the current time.</summary>
public sealed record StreakStatistics
{
    /// <summary>The player.</summary>
    public required Guid PlayerId { get; init; }

    /// <summary>The streak definition.</summary>
    public required string DefinitionId { get; init; }

    /// <summary>Current run length.</summary>
    public int CurrentCount { get; init; }

    /// <summary>Longest run ever.</summary>
    public int LongestCount { get; init; }

    /// <summary>When the longest run was reached.</summary>
    public DateTimeOffset? LongestCountAchievedAt { get; init; }

    /// <summary>Qualifying activities accepted.</summary>
    public long TotalQualifyingActivities { get; init; }

    /// <summary>Periods (or activities for per-activity streaks) that increased a streak.</summary>
    public long TotalQualifiedPeriods { get; init; }

    /// <summary>How often the streak was broken.</summary>
    public int TimesBroken { get; init; }

    /// <summary>Freezes consumed.</summary>
    public int FreezesUsed { get; init; }

    /// <summary>Freezes available.</summary>
    public int FreezesAvailable { get; init; }

    /// <summary>Milestones ever reached.</summary>
    public IReadOnlyList<int> AllTimeMilestones { get; init; } = [];

    /// <summary>Earliest qualifying activity.</summary>
    public DateTimeOffset? FirstActivityAt { get; init; }

    /// <summary>Latest qualifying activity.</summary>
    public DateTimeOffset? LastQualifyingActivityAt { get; init; }
}
