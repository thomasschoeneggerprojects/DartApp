namespace SportTA.Streaks;

/// <summary>Base type for streak library exceptions.</summary>
public class StreakException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public StreakException()
    {
    }

    /// <summary>Initializes a new instance with a message.</summary>
    public StreakException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    public StreakException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
