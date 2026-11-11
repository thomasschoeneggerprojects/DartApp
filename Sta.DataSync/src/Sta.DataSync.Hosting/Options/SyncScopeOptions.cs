namespace Sta.DataSync.Hosting;

/// <summary>Per-scope settings used by the registration methods.</summary>
public class SyncScopeOptions
{
    /// <summary>
    /// Destination of the scope. <see langword="null"/> selects the only registered provider; when several providers are
    /// registered, every scope must name its provider (no implicit fan-out).
    /// </summary>
    public string? ProviderId { get; set; }

    /// <summary>Scope id; defaults to the source id.</summary>
    public string? ScopeId { get; set; }

    /// <summary>Mode; <see langword="null"/> uses <see cref="DataSyncOptions.DefaultMode"/>.</summary>
    public SyncMode? Mode { get; set; }

    /// <summary>Conflict strategy; <see langword="null"/> uses the default.</summary>
    public ConflictResolutionStrategy? ConflictStrategy { get; set; }

    /// <summary>Deletion policy; <see langword="null"/> uses the default.</summary>
    public DeletionPolicy? DeletionPolicy { get; set; }
}

/// <summary>Settings of a file scope.</summary>
public sealed class FileSyncScopeOptions : SyncScopeOptions
{
    /// <summary>Included glob patterns (default: everything).</summary>
    public IList<string> Include { get; } = [];

    /// <summary>Excluded glob patterns.</summary>
    public IList<string> Exclude { get; } = [];

    /// <summary>Watch the directory and trigger synchronization on changes (needs the scheduler).</summary>
    public bool WatchForChanges { get; set; }
}

/// <summary>Settings of a SQLite snapshot scope.</summary>
public sealed class SqliteSyncScopeOptions : SyncScopeOptions
{
    /// <summary>Item id of the snapshot (default: database file name).</summary>
    public string? ItemId { get; set; }

    /// <summary>Called before a downloaded snapshot is restored.</summary>
    public Func<CancellationToken, Task>? BeforeRestoreAsync { get; set; }

    /// <summary>Called after a restore.</summary>
    public Func<CancellationToken, Task>? AfterRestoreAsync { get; set; }
}

/// <summary>Scheduling of automatic synchronization.</summary>
public sealed class SyncScheduleOptions
{
    /// <summary>Synchronize when the scheduler starts.</summary>
    public bool SyncOnStartup { get; set; } = true;

    /// <summary>Periodic synchronization interval; <see cref="TimeSpan.Zero"/> disables it.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Synchronize after local changes (reported by sources or <see cref="ISyncScheduler.NotifyLocalChange"/>).</summary>
    public bool SyncOnLocalChanges { get; set; } = true;

    /// <summary>Synchronize automatically only on unmetered connections. Manual synchronization is not restricted.</summary>
    public bool WifiOnly { get; set; }

    /// <summary>Quiet time after the last local change before synchronizing.</summary>
    public TimeSpan ChangeDebounce { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Synchronize when the connection comes back.</summary>
    public bool SyncOnConnectivityRestored { get; set; } = true;

    /// <summary>First retry delay after a transient failure (doubles, with jitter).</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest retry delay.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Returns validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Interval != TimeSpan.Zero && Interval < TimeSpan.FromMinutes(1)) errors.Add("Interval must be zero (disabled) or at least one minute.");
        if (ChangeDebounce < TimeSpan.Zero || ChangeDebounce > TimeSpan.FromMinutes(10)) errors.Add("ChangeDebounce must be between 0 and 10 minutes.");
        if (RetryBaseDelay <= TimeSpan.Zero) errors.Add("RetryBaseDelay must be positive.");
        if (RetryMaxDelay < RetryBaseDelay) errors.Add("RetryMaxDelay must not be smaller than RetryBaseDelay.");
        return errors;
    }
}
