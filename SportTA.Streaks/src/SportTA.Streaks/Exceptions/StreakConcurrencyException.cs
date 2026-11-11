namespace SportTA.Streaks;

/// <summary>Thrown by repositories when a streak was modified concurrently (optimistic concurrency conflict).</summary>
public sealed class StreakConcurrencyException : StreakException
{
    /// <summary>Initializes a new instance.</summary>
    public StreakConcurrencyException(string message)
        : base(message)
    {
    }
}
