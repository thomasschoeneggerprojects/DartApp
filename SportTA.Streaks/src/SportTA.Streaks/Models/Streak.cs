using System.Collections.Immutable;

namespace SportTA.Streaks;

/// <summary>
/// The persisted state of one streak for one player. Immutable; every change produces a new instance with a higher
/// <see cref="Version"/>. Custom <see cref="IStreakRepository"/> implementations must store every property.
/// All timestamps are UTC.
/// </summary>
public sealed record Streak
{
    /// <summary>Unique identifier of this streak record.</summary>
    public required Guid Id { get; init; }

    /// <summary>Identifier of the <see cref="StreakDefinition"/>.</summary>
    public required string DefinitionId { get; init; }

    /// <summary>The player or user this streak belongs to.</summary>
    public required Guid PlayerId { get; init; }

    /// <summary>Current status.</summary>
    public StreakStatus Status { get; init; }

    /// <summary>Length of the current run.</summary>
    public int CurrentCount { get; init; }

    /// <summary>Longest run ever reached. Never decreases.</summary>
    public int LongestCount { get; init; }

    /// <summary>When <see cref="LongestCount"/> was reached.</summary>
    public DateTimeOffset? LongestCountAchievedAt { get; init; }

    /// <summary>Number of qualifying activities accepted, including several per period.</summary>
    public long TotalQualifyingActivities { get; init; }

    /// <summary>Number of periods (or activities for per-activity streaks) that increased a streak.</summary>
    public long TotalQualifiedPeriods { get; init; }

    /// <summary>How often the streak was broken.</summary>
    public int TimesBroken { get; init; }

    /// <summary>When the current run started.</summary>
    public DateTimeOffset? CurrentStreakStartedAt { get; init; }

    /// <summary>Earliest qualifying activity ever accepted.</summary>
    public DateTimeOffset? FirstActivityAt { get; init; }

    /// <summary>Latest qualifying activity accepted.</summary>
    public DateTimeOffset? LastQualifyingActivityAt { get; init; }

    /// <summary>Latest activity timestamp processed (qualifying or failing). Used to reject out-of-order activities.</summary>
    public DateTimeOffset? LastProcessedActivityAt { get; init; }

    /// <summary>Index of the last period whose requirement was met (calendar streaks only).</summary>
    public long? LastQualifiedPeriod { get; init; }

    /// <summary>Last evaluated period: every period up to and including this index is settled (qualified, covered or missed).</summary>
    public long? SettledThroughPeriod { get; init; }

    /// <summary>Period that is currently collecting activities.</summary>
    public long? OpenPeriod { get; init; }

    /// <summary>Activities (or distinct days) counted in <see cref="OpenPeriod"/>.</summary>
    public int OpenPeriodActivityCount { get; init; }

    /// <summary>Distinct local day numbers counted in <see cref="OpenPeriod"/> (distinct-day weekly streaks).</summary>
    public IReadOnlyList<int> OpenPeriodActiveDays { get; init; } = [];

    /// <summary>Grace periods used since the last qualifying period.</summary>
    public int ConsecutiveMissedPeriods { get; init; }

    /// <summary>Freezes the player currently holds.</summary>
    public int FreezesAvailable { get; init; }

    /// <summary>Freezes consumed over the lifetime of this streak.</summary>
    public int FreezesUsed { get; init; }

    /// <summary>Count of the most recently broken run that can still be restored.</summary>
    public int? RestorableCount { get; init; }

    /// <summary>Start of the most recently broken run.</summary>
    public DateTimeOffset? RestorableStartedAt { get; init; }

    /// <summary>Milestones reached in the most recently broken run.</summary>
    public IReadOnlyList<int> RestorableMilestones { get; init; } = [];

    /// <summary>When the streak was last broken.</summary>
    public DateTimeOffset? BrokenAt { get; init; }

    /// <summary>Milestones reached in the current run (prevents repeated rewards).</summary>
    public IReadOnlyList<int> CurrentRunMilestones { get; init; } = [];

    /// <summary>Milestones ever reached.</summary>
    public IReadOnlyList<int> AllTimeMilestones { get; init; } = [];

    /// <summary>Most recently processed activity identifiers, oldest first (idempotency window).</summary>
    public IReadOnlyList<string> RecentActivityIds { get; init; } = [];

    /// <summary>Application-defined metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>When this record was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When this record was last modified.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Optimistic-concurrency version. 0 means "never stored".</summary>
    public long Version { get; init; }

    internal static Streak CreateNew(Guid playerId, string definitionId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        DefinitionId = definitionId,
        PlayerId = playerId,
        Status = StreakStatus.NotStarted,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
