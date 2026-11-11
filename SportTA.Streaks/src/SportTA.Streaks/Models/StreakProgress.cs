namespace SportTA.Streaks;

/// <summary>
/// A read-only snapshot for UIs, evaluated against the current time without persisting anything.
/// </summary>
public sealed record StreakProgress
{
    /// <summary>The player.</summary>
    public required Guid PlayerId { get; init; }

    /// <summary>The streak definition.</summary>
    public required string DefinitionId { get; init; }

    /// <summary>Display name of the definition.</summary>
    public required string Name { get; init; }

    /// <summary>Stored record id, or <see langword="null"/> when nothing has been stored yet.</summary>
    public Guid? StreakId { get; init; }

    /// <summary>Period type.</summary>
    public required StreakPeriod Period { get; init; }

    /// <summary>Status as of now.</summary>
    public required StreakStatus Status { get; init; }

    /// <summary>Length of the current run.</summary>
    public int CurrentCount { get; init; }

    /// <summary>Longest run ever.</summary>
    public int LongestCount { get; init; }

    /// <summary>Total qualifying activities.</summary>
    public long TotalQualifyingActivities { get; init; }

    /// <summary>Activities (or distinct days) required per period.</summary>
    public int RequiredActivities { get; init; }

    /// <summary>Activities (or distinct days) counted in the current period.</summary>
    public int ActivitiesInCurrentPeriod { get; init; }

    /// <summary>Whether the current period's requirement is already met.</summary>
    public bool IsCurrentPeriodComplete { get; init; }

    /// <summary>Whether the streak breaks (or consumes grace/freezes) if the current period is not completed.</summary>
    public bool IsAtRisk { get; init; }

    /// <summary>Start of the current calendar period (UTC), or <see langword="null"/> for per-activity streaks.</summary>
    public DateTimeOffset? CurrentPeriodStart { get; init; }

    /// <summary>End (exclusive) of the current calendar period (UTC), or <see langword="null"/> for per-activity streaks.</summary>
    public DateTimeOffset? CurrentPeriodEnd { get; init; }

    /// <summary>When the current run started.</summary>
    public DateTimeOffset? CurrentStreakStartedAt { get; init; }

    /// <summary>Latest qualifying activity.</summary>
    public DateTimeOffset? LastQualifyingActivityAt { get; init; }

    /// <summary>Freezes available.</summary>
    public int FreezesAvailable { get; init; }

    /// <summary>Grace periods still available before a miss consumes freezes or breaks the streak.</summary>
    public int GracePeriodsRemaining { get; init; }

    /// <summary>Next milestone above the current count, if any.</summary>
    public int? NextMilestone { get; init; }

    /// <summary>Milestones reached in the current run.</summary>
    public IReadOnlyList<int> ReachedMilestones { get; init; } = [];

    /// <summary>Count of a broken run that can be restored with <see cref="IStreakService.RestoreAsync"/>.</summary>
    public int? RestorableCount { get; init; }

    /// <summary>When the streak was last broken.</summary>
    public DateTimeOffset? BrokenAt { get; init; }

    /// <summary>Application-defined metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
