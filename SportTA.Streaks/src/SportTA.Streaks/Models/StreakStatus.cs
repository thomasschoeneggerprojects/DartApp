namespace SportTA.Streaks;

/// <summary>Lifecycle state of a streak.</summary>
public enum StreakStatus
{
    /// <summary>No streak has been started yet (or it was reset).</summary>
    NotStarted = 0,

    /// <summary>The streak is running.</summary>
    Active = 1,

    /// <summary>The streak is running but one or more grace periods have been used since the last qualifying period.</summary>
    InGrace = 2,

    /// <summary>The streak ended because a period was missed or a failure occurred. It can be restored with <see cref="IStreakService.RestoreAsync"/>.</summary>
    Broken = 3,
}
