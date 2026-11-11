namespace Sta.DataSync;

/// <summary>Persistent state of one scope (internal synchronization metadata).</summary>
public sealed record ScopeSyncState
{
    /// <summary>Scope id.</summary>
    public required string ScopeId { get; init; }

    /// <summary>Provider the state belongs to; a different provider starts from scratch.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Revision of the last manifest this device committed or applied.</summary>
    public long ManifestRevision { get; init; }

    /// <summary>Remote path of that manifest (identifies the winning revision file).</summary>
    public string? ManifestPath { get; init; }

    /// <summary>Remote path of the last manifest this device itself committed; used to detect lost commits.</summary>
    public string? OwnCommitPath { get; init; }

    /// <summary>
    /// Baselines before this device's last commit (JSON written by the engine). Restored if that commit turns out to
    /// have lost a concurrent race, so its changes are detected again instead of being overwritten.
    /// </summary>
    public string? OwnCommitRollback { get; init; }

    /// <summary>Provider change token (e.g. a Google Drive page token).</summary>
    public string? ChangeToken { get; init; }

    /// <summary>Last successful run (UTC).</summary>
    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>Last run of any outcome (UTC).</summary>
    public DateTimeOffset? LastAttemptUtc { get; init; }

    /// <summary>Last error, cleared after success.</summary>
    public string? LastError { get; init; }

    /// <summary>Last remote garbage collection (UTC).</summary>
    public DateTimeOffset? LastCleanupUtc { get; init; }
}

/// <summary>Persistent state of one item: the last common version (baseline) and a hash cache.</summary>
public sealed record ItemSyncState
{
    /// <summary>Scope id.</summary>
    public required string ScopeId { get; init; }

    /// <summary>Item id.</summary>
    public required string ItemId { get; init; }

    /// <summary>Hash both sides agreed on at the last sync; <see langword="null"/> if never synchronized or deleted.</summary>
    public string? BaseHash { get; init; }

    /// <summary>Length of the baseline content.</summary>
    public long BaseLength { get; init; }

    /// <summary>Remote item version of the baseline (manifest entry version).</summary>
    public long BaseVersion { get; init; }

    /// <summary><see langword="true"/> when the baseline is a deletion (tombstone).</summary>
    public bool BaseDeleted { get; init; }

    /// <summary>Local fingerprint at the time <see cref="LocalHash"/> was computed.</summary>
    public string? LocalFingerprint { get; init; }

    /// <summary>Cached hash of the local content for <see cref="LocalFingerprint"/>.</summary>
    public string? LocalHash { get; init; }

    /// <summary><see langword="true"/> when a baseline exists (the item was synchronized at least once).</summary>
    public bool HasBaseline => BaseHash is not null || BaseDeleted;
}

/// <summary>Journal entry of a synchronization session, used for crash recovery.</summary>
/// <param name="SessionId">Session id.</param>
/// <param name="ScopeId">Scope id.</param>
/// <param name="Phase">Phase name written by the engine (e.g. <c>Executing</c>, <c>Committing</c>).</param>
/// <param name="Payload">Phase data (JSON) needed to finish or roll back the session.</param>
/// <param name="UpdatedUtc">Last update.</param>
public sealed record SyncJournalEntry(string SessionId, string ScopeId, string Phase, string? Payload, DateTimeOffset UpdatedUtc);

/// <summary>A set of state changes written in one transaction.</summary>
public sealed record SyncStateCommit
{
    /// <summary>The scope.</summary>
    public required string ScopeId { get; init; }

    /// <summary>New scope state, or <see langword="null"/> to keep it.</summary>
    public ScopeSyncState? Scope { get; init; }

    /// <summary>Item states to insert or replace.</summary>
    public IReadOnlyList<ItemSyncState> UpsertItems { get; init; } = [];

    /// <summary>Item ids whose state is removed.</summary>
    public IReadOnlyList<string> RemoveItems { get; init; } = [];

    /// <summary>Conflicts to store (insert or replace).</summary>
    public IReadOnlyList<SyncConflict> UpsertConflicts { get; init; } = [];

    /// <summary>Item ids whose stored conflict is removed.</summary>
    public IReadOnlyList<string> RemoveConflicts { get; init; } = [];

    /// <summary>Journal session finished by this commit, if any.</summary>
    public string? CompleteJournalSession { get; init; }
}
