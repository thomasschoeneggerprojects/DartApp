namespace SportTA.Streaks;

/// <summary>Defines how activities are grouped when a streak is evaluated.</summary>
public enum StreakPeriod
{
    /// <summary>One calendar day in the player's time zone. Requirements must be met every day.</summary>
    Daily = 0,

    /// <summary>One calendar week in the player's time zone, starting on <see cref="StreakDefinition.WeekStartDay"/>.</summary>
    Weekly = 1,

    /// <summary>No calendar periods: every qualifying activity extends the streak and failures can break it (e.g. win streaks).</summary>
    PerActivity = 2,
}
