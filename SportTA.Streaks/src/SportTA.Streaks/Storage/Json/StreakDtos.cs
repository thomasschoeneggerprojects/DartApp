namespace SportTA.Streaks.Storage.Json;

/// <summary>On-disk document: all streaks of one player. Kept separate from the public model so the file format stays stable.</summary>
internal sealed class StreakDocumentDto
{
    public int FormatVersion { get; set; }

    public Guid PlayerId { get; set; }

    public List<StreakRecordDto> Streaks { get; set; } = [];
}

internal sealed class StreakRecordDto
{
    public Guid Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public Guid PlayerId { get; set; }

    public StreakStatus Status { get; set; }

    public int CurrentCount { get; set; }

    public int LongestCount { get; set; }

    public DateTimeOffset? LongestCountAchievedAt { get; set; }

    public long TotalQualifyingActivities { get; set; }

    public long TotalQualifiedPeriods { get; set; }

    public int TimesBroken { get; set; }

    public DateTimeOffset? CurrentStreakStartedAt { get; set; }

    public DateTimeOffset? FirstActivityAt { get; set; }

    public DateTimeOffset? LastQualifyingActivityAt { get; set; }

    public DateTimeOffset? LastProcessedActivityAt { get; set; }

    public long? LastQualifiedPeriod { get; set; }

    public long? SettledThroughPeriod { get; set; }

    public long? OpenPeriod { get; set; }

    public int OpenPeriodActivityCount { get; set; }

    public List<int>? OpenPeriodActiveDays { get; set; }

    public int ConsecutiveMissedPeriods { get; set; }

    public int FreezesAvailable { get; set; }

    public int FreezesUsed { get; set; }

    public int? RestorableCount { get; set; }

    public DateTimeOffset? RestorableStartedAt { get; set; }

    public List<int>? RestorableMilestones { get; set; }

    public DateTimeOffset? BrokenAt { get; set; }

    public List<int>? CurrentRunMilestones { get; set; }

    public List<int>? AllTimeMilestones { get; set; }

    public List<string>? RecentActivityIds { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public long Version { get; set; }
}
