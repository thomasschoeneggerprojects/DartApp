namespace Sta.DataSync;

/// <summary>
/// Persistent synchronization metadata (baselines, hashes, tombstone knowledge, journal, conflicts, provider tokens).
/// Kept separate from the application's own data. Implementations must be thread-safe and write each
/// <see cref="SyncStateCommit"/> atomically.
/// </summary>
public interface ISyncStateStore
{
    /// <summary>Stable id of this device installation (created on first use).</summary>
    Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default);

    /// <summary>State of a scope, or <see langword="null"/> before its first synchronization.</summary>
    Task<ScopeSyncState?> GetScopeAsync(string scopeId, CancellationToken cancellationToken = default);

    /// <summary>All item states of a scope, keyed by item id (ordinal).</summary>
    Task<IReadOnlyDictionary<string, ItemSyncState>> GetItemsAsync(string scopeId, CancellationToken cancellationToken = default);

    /// <summary>Stored unresolved conflicts of a scope, or of all scopes when <paramref name="scopeId"/> is <see langword="null"/>.</summary>
    Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(string? scopeId, CancellationToken cancellationToken = default);

    /// <summary>Decisions the application made for stored conflicts, keyed by item id.</summary>
    Task<IReadOnlyDictionary<string, ConflictResolution>> GetConflictResolutionsAsync(string scopeId, CancellationToken cancellationToken = default);

    /// <summary>Stores the application's decision for a stored conflict; applied by the next synchronization.</summary>
    /// <exception cref="SyncItemNotFoundException">No conflict is stored for the item.</exception>
    Task SetConflictResolutionAsync(string scopeId, string itemId, ConflictResolution resolution, CancellationToken cancellationToken = default);

    /// <summary>The unfinished journal entry of a scope, if a session was interrupted.</summary>
    Task<SyncJournalEntry?> GetOpenJournalAsync(string scopeId, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a journal entry (durably, before the step it describes).</summary>
    Task WriteJournalAsync(SyncJournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Removes a journal entry without other changes (e.g. after a rollback).</summary>
    Task DeleteJournalAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Writes all changes of <paramref name="commit"/> in one transaction.</summary>
    Task CommitAsync(SyncStateCommit commit, CancellationToken cancellationToken = default);

    /// <summary>Deletes all state of a scope (e.g. after switching provider or signing out).</summary>
    Task ResetScopeAsync(string scopeId, CancellationToken cancellationToken = default);
}

/// <summary>Synchronizes the registered scopes. Thread-safe; runs of the same scope never overlap.</summary>
public interface IDataSyncService
{
    /// <summary>Ids of all registered scopes.</summary>
    IReadOnlyList<string> ScopeIds { get; }

    /// <summary>Synchronizes all scopes (independent scopes run concurrently).</summary>
    Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Synchronizes one scope (by default the scope id equals the data source id).</summary>
    Task<SyncResult> SynchronizeAsync(string scopeId, CancellationToken cancellationToken = default);

    /// <summary>Synchronizes one scope, or all when <paramref name="scopeId"/> is <see langword="null"/>, reporting progress.</summary>
    Task<SyncResult> SynchronizeAsync(string? scopeId, IProgress<SyncProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>Current activity and per-scope state.</summary>
    Task<SyncStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Unresolved conflicts (of one scope or all).</summary>
    Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(string? scopeId = null, CancellationToken cancellationToken = default);

    /// <summary>Records a decision for a conflict; the next synchronization of the scope applies it.</summary>
    Task ResolveConflictAsync(string scopeId, string itemId, ConflictResolution resolution, CancellationToken cancellationToken = default);

    /// <summary>Raised when the activity or a scope's state changes. May be raised on any thread.</summary>
    event EventHandler<SyncStatusChangedEventArgs>? StatusChanged;

    /// <summary>Raised while a synchronization makes progress. May be raised on any thread.</summary>
    event EventHandler<SyncProgressEventArgs>? ProgressChanged;

    /// <summary>Raised after every synchronization run. May be raised on any thread.</summary>
    event EventHandler<SyncCompletedEventArgs>? SyncCompleted;
}
