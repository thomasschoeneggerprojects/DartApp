namespace SportTA.Streaks;

/// <summary>
/// Configuration supplied by the hosting application. Values are copied when services are created, so later changes
/// to an options instance do not affect running services.
/// </summary>
public sealed class StreakOptions
{
    /// <summary>
    /// Absolute path of the application's existing data directory, used by the default JSON storage.
    /// The library never chooses a directory on its own. Not needed when a custom repository or storage provider is used.
    /// </summary>
    public string? StorageDirectory { get; set; }

    /// <summary>Time zone for daily/weekly calculations. Defaults to UTC; the library never uses the machine's local zone implicitly.</summary>
    public TimeZoneInfo DefaultTimeZone { get; set; } = TimeZoneInfo.Utc;

    /// <summary>Optional per-player time zone. Falls back to <see cref="DefaultTimeZone"/> when not set.</summary>
    public Func<Guid, TimeZoneInfo>? PlayerTimeZoneResolver { get; set; }

    /// <summary>Number of recent activity ids remembered per streak for idempotency. Default 256.</summary>
    public int ProcessedActivityHistorySize { get; set; } = 256;

    /// <summary>How far an activity timestamp may lie in the future (clock skew between devices). Default 5 minutes.</summary>
    public TimeSpan MaxFutureTimestampSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Retries after an optimistic concurrency conflict. Default 5.</summary>
    public int MaxConcurrencyRetries { get; set; } = 5;

    /// <summary>Behavior of the JSON repository for corrupted data. Default <see cref="CorruptDataHandling.Throw"/>.</summary>
    public CorruptDataHandling CorruptDataHandling { get; set; } = CorruptDataHandling.Throw;

    /// <summary>How long the file storage waits for a lock held by another process. Default 10 seconds.</summary>
    public TimeSpan StorageLockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Definitions registered when the streak system is added to a service collection.</summary>
    public IList<StreakDefinition> Definitions { get; } = new List<StreakDefinition>();

    /// <summary>Validates the options.</summary>
    /// <exception cref="ArgumentException">An option is invalid.</exception>
    public void Validate()
    {
        if (DefaultTimeZone is null)
        {
            throw new ArgumentException("DefaultTimeZone must be set.", nameof(DefaultTimeZone));
        }

        if (StorageDirectory is not null && !Path.IsPathFullyQualified(StorageDirectory))
        {
            throw new ArgumentException("StorageDirectory must be an absolute path supplied by the hosting application.", nameof(StorageDirectory));
        }

        if (ProcessedActivityHistorySize < 1)
        {
            throw new ArgumentException("ProcessedActivityHistorySize must be at least 1.", nameof(ProcessedActivityHistorySize));
        }

        if (MaxFutureTimestampSkew < TimeSpan.Zero)
        {
            throw new ArgumentException("MaxFutureTimestampSkew must not be negative.", nameof(MaxFutureTimestampSkew));
        }

        if (MaxConcurrencyRetries < 0)
        {
            throw new ArgumentException("MaxConcurrencyRetries must not be negative.", nameof(MaxConcurrencyRetries));
        }

        if (StorageLockTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("StorageLockTimeout must be positive.", nameof(StorageLockTimeout));
        }

        if (!Enum.IsDefined(CorruptDataHandling))
        {
            throw new ArgumentException("Unknown CorruptDataHandling value.", nameof(CorruptDataHandling));
        }
    }
}
