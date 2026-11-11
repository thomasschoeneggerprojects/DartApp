namespace SportTA.Streaks;

/// <summary>The decision of an <see cref="IStreakRule"/> about how an activity affects a streak.</summary>
public enum ActivityDecision
{
    /// <summary>The activity does not affect this streak.</summary>
    Ignore = 0,

    /// <summary>The activity counts towards this streak.</summary>
    Qualify = 1,

    /// <summary>The activity breaks this streak (grace periods and freezes can absorb it).</summary>
    Fail = 2,
}
