namespace SportTA.Streaks;

/// <summary>What the JSON repository does when stored data is corrupted and no valid backup exists.</summary>
public enum CorruptDataHandling
{
    /// <summary>Throw <see cref="StreakDataCorruptedException"/> and leave the data untouched (default).</summary>
    Throw = 0,

    /// <summary>Move the corrupted file aside (it is never deleted), log an error and continue with empty data.</summary>
    QuarantineAndReset = 1,
}
