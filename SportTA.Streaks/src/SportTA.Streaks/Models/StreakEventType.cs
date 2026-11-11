namespace SportTA.Streaks;

/// <summary>Kinds of streak notifications.</summary>
public enum StreakEventType
{
    /// <summary>A new streak run started (count went from 0 to 1).</summary>
    Started,

    /// <summary>The current streak count increased.</summary>
    Increased,

    /// <summary>A configured milestone was reached in the current run.</summary>
    MilestoneReached,

    /// <summary>The streak ended.</summary>
    Broken,

    /// <summary>A broken streak was restored by the application.</summary>
    Restored,

    /// <summary>One or more freezes were consumed to protect the streak.</summary>
    Frozen,

    /// <summary>The streak was reset by the application.</summary>
    Reset,
}
