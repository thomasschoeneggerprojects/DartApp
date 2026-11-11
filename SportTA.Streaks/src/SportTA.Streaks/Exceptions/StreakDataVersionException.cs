namespace SportTA.Streaks;

/// <summary>
/// Thrown when stored data was written by a newer library version. The data is never modified or downgraded.
/// </summary>
public sealed class StreakDataVersionException : StreakStorageException
{
    /// <summary>Initializes a new instance.</summary>
    public StreakDataVersionException(int foundVersion, int supportedVersion)
        : base($"Streak data format version {foundVersion} is newer than the supported version {supportedVersion}. Update the SportTA.Streaks library.")
    {
        FoundVersion = foundVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>Version found in storage.</summary>
    public int FoundVersion { get; }

    /// <summary>Newest version this library understands.</summary>
    public int SupportedVersion { get; }
}
