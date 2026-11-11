namespace Sta.DataSync;

/// <summary>Engine options (bind with the Options pattern; validated by <see cref="Validate"/>).</summary>
public sealed class DataSyncOptions
{
    /// <summary>Logical application id, e.g. <c>DartTA</c>; isolates the application's data below <see cref="RootFolder"/>.</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>Top-level remote folder of all Sta.DataSync data.</summary>
    public string RootFolder { get; set; } = "Sta.DataSync";

    /// <summary>Mode for scopes that do not set their own.</summary>
    public SyncMode DefaultMode { get; set; } = SyncMode.Bidirectional;

    /// <summary>Conflict strategy for scopes that do not set their own.</summary>
    public ConflictResolutionStrategy DefaultConflictStrategy { get; set; } = ConflictResolutionStrategy.KeepBoth;

    /// <summary>Deletion policy for scopes that do not set their own.</summary>
    public DeletionPolicy DefaultDeletionPolicy { get; set; } = DeletionPolicy.Propagate;

    /// <summary>Concurrent transfers per scope (1-16).</summary>
    public int MaxParallelTransfers { get; set; } = 2;

    /// <summary>Retry behavior for transient failures.</summary>
    public SyncRetryOptions Retry { get; set; } = new();

    /// <summary>
    /// Wait between writing a manifest revision and verifying that it won (providers without conditional writes).
    /// Gives concurrent writers' files time to become visible.
    /// </summary>
    public TimeSpan CommitVerificationDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a run re-plans after losing a concurrent commit (1-10).</summary>
    public int MaxCommitAttempts { get; set; } = 3;

    /// <summary>Manifest revisions kept remotely (at least 2).</summary>
    public int KeepManifestRevisions { get; set; } = 10;

    /// <summary>Minimum time between remote cleanups of a scope.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Unreferenced remote objects and old manifests younger than this are never deleted.</summary>
    public TimeSpan ObjectGracePeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long tombstones stay in the manifest. Devices offline for longer may re-upload deleted items.</summary>
    public TimeSpan TombstoneRetention { get; set; } = TimeSpan.FromDays(180);

    /// <summary>Largest accepted manifest (defensive limit for remote metadata).</summary>
    public long MaxManifestBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Returns all validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IsSegment(ApplicationId)) errors.Add("ApplicationId must be a single valid path segment, e.g. 'DartTA'.");
        if (!IsSegment(RootFolder)) errors.Add("RootFolder must be a single valid path segment.");
        if (!Enum.IsDefined(DefaultMode)) errors.Add("DefaultMode is not defined.");
        if (!Enum.IsDefined(DefaultConflictStrategy)) errors.Add("DefaultConflictStrategy is not defined.");
        if (!Enum.IsDefined(DefaultDeletionPolicy)) errors.Add("DefaultDeletionPolicy is not defined.");
        if (MaxParallelTransfers is < 1 or > 16) errors.Add("MaxParallelTransfers must be between 1 and 16.");
        if (MaxCommitAttempts is < 1 or > 10) errors.Add("MaxCommitAttempts must be between 1 and 10.");
        if (KeepManifestRevisions < 2) errors.Add("KeepManifestRevisions must be at least 2.");
        if (CommitVerificationDelay < TimeSpan.Zero) errors.Add("CommitVerificationDelay must not be negative.");
        if (CleanupInterval < TimeSpan.Zero) errors.Add("CleanupInterval must not be negative.");
        if (ObjectGracePeriod < TimeSpan.FromMinutes(10)) errors.Add("ObjectGracePeriod must be at least 10 minutes.");
        if (TombstoneRetention < TimeSpan.FromDays(1)) errors.Add("TombstoneRetention must be at least one day.");
        if (MaxManifestBytes < 1024) errors.Add("MaxManifestBytes must be at least 1 KB.");
        if (Retry is null) errors.Add("Retry is required.");
        else errors.AddRange(Retry.Validate());
        return errors;
    }

    internal static bool IsSegment(string? value) => SyncPath.IsValid(value) && !value!.Contains('/', StringComparison.Ordinal);
}

/// <summary>Exponential backoff with jitter for transient failures.</summary>
public sealed class SyncRetryOptions
{
    /// <summary>Attempts per operation including the first (1-10).</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Delay before the first retry; doubles per attempt.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound of a single delay (also caps server <c>Retry-After</c>).</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Returns all validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaxAttempts is < 1 or > 10) errors.Add("Retry.MaxAttempts must be between 1 and 10.");
        if (BaseDelay < TimeSpan.Zero) errors.Add("Retry.BaseDelay must not be negative.");
        if (MaxDelay < BaseDelay) errors.Add("Retry.MaxDelay must not be smaller than Retry.BaseDelay.");
        return errors;
    }
}

/// <summary>A configured synchronization scope: one data source, one destination, one mode.</summary>
public sealed class SyncScopeDefinition
{
    /// <summary>Creates a scope.</summary>
    public SyncScopeDefinition(string scopeId, ISyncDataSource source, ISyncProvider provider)
    {
        if (!DataSyncOptions.IsSegment(scopeId)) throw new ArgumentException("The scope id must be a single valid path segment.", nameof(scopeId));
        ScopeId = scopeId;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>Scope id (remote folder name below the application folder).</summary>
    public string ScopeId { get; }

    /// <summary>What is synchronized.</summary>
    public ISyncDataSource Source { get; }

    /// <summary>Where it is stored.</summary>
    public ISyncProvider Provider { get; }

    /// <summary>Mode; <see langword="null"/> uses <see cref="DataSyncOptions.DefaultMode"/>.</summary>
    public SyncMode? Mode { get; init; }

    /// <summary>Conflict strategy; <see langword="null"/> uses the default.</summary>
    public ConflictResolutionStrategy? ConflictStrategy { get; init; }

    /// <summary>Deletion policy; <see langword="null"/> uses the default.</summary>
    public DeletionPolicy? DeletionPolicy { get; init; }

    /// <summary>Optional content transform (e.g. future encryption).</summary>
    public ISyncContentTransform? ContentTransform { get; init; }
}
