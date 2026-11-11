namespace SportTA.Streaks;

/// <summary>The result of a streak operation.</summary>
public sealed record StreakResult
{
    /// <summary>Primary outcome.</summary>
    public required StreakResultCode Code { get; init; }

    /// <summary>Progress after the operation, evaluated against the current time.</summary>
    public required StreakProgress Progress { get; init; }

    /// <summary>Events produced by this operation, in order. They were persisted before being returned.</summary>
    public IReadOnlyList<StreakEvent> Events { get; init; } = [];

    /// <summary>Whether the operation changed and persisted state.</summary>
    public bool StateChanged { get; init; }
}
