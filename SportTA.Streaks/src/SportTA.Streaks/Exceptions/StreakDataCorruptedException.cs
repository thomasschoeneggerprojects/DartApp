namespace SportTA.Streaks;

/// <summary>
/// Thrown when stored streak data is unreadable and no valid backup exists. The data is left untouched so it can be
/// inspected or repaired. Configure <see cref="CorruptDataHandling.QuarantineAndReset"/> to continue with empty data instead.
/// </summary>
public sealed class StreakDataCorruptedException : StreakStorageException
{
    /// <summary>Initializes a new instance.</summary>
    public StreakDataCorruptedException(string storageKey, Exception? innerException)
        : base($"Streak data '{storageKey}' is corrupted and no valid backup is available.", innerException)
    {
        StorageKey = storageKey;
    }

    /// <summary>The storage key of the corrupted data.</summary>
    public string StorageKey { get; }
}
