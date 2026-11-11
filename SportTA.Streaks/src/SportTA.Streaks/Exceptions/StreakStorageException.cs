namespace SportTA.Streaks;

/// <summary>Thrown when streak data cannot be read or written.</summary>
public class StreakStorageException : StreakException
{
    /// <summary>Initializes a new instance.</summary>
    public StreakStorageException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with an inner exception.</summary>
    public StreakStorageException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
