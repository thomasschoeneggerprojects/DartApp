namespace SportTA.Streaks;

/// <summary>Application-reported outcome of an activity.</summary>
public enum ActivityOutcome
{
    /// <summary>The activity was completed successfully (e.g. a workout was done or a match was won).</summary>
    Success = 0,

    /// <summary>The activity failed (e.g. a match was lost).</summary>
    Failure = 1,

    /// <summary>The activity neither extends nor breaks a streak (e.g. a draw).</summary>
    Neutral = 2,
}
