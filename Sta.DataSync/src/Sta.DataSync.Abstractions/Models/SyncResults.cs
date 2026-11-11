namespace Sta.DataSync;

/// <summary>Progress of a running synchronization.</summary>
/// <param name="CompletedItems">Operations finished so far.</param>
/// <param name="TotalItems">Operations planned.</param>
/// <param name="TransferredBytes">Bytes uploaded and downloaded so far.</param>
/// <param name="CurrentItem">Item being processed, if any.</param>
public sealed record SyncProgress(int CompletedItems, int TotalItems, long TransferredBytes, string? CurrentItem)
{
    /// <summary>Id of the scope the progress belongs to.</summary>
    public string? ScopeId { get; init; }
}

/// <summary>Result of one planned operation.</summary>
/// <param name="ItemId">The item.</param>
/// <param name="Kind">What was done.</param>
/// <param name="Succeeded"><see langword="true"/> when the operation completed and was committed.</param>
/// <param name="TransferredBytes">Bytes moved by this operation.</param>
/// <param name="Error">Failure reason; never contains credentials or file content.</param>
public sealed record SyncOperationResult(
    string ItemId,
    SyncOperationKind Kind,
    bool Succeeded,
    long TransferredBytes = 0,
    string? Error = null);

/// <summary>A failure that a later synchronization can recover from (the item stays pending).</summary>
/// <param name="ScopeId">The scope.</param>
/// <param name="ItemId">The item, or <see langword="null"/> for scope-level failures.</param>
/// <param name="Message">What went wrong.</param>
/// <param name="IsTransient"><see langword="true"/> for temporary failures (network, rate limit).</param>
public sealed record SyncFailure(string ScopeId, string? ItemId, string Message, bool IsTransient);

/// <summary>Result of a synchronization run (one scope, or the aggregate of all scopes).</summary>
public sealed record SyncResult
{
    /// <summary>Overall outcome.</summary>
    public required SyncResultStatus Status { get; init; }

    /// <summary>Scope id, or <see langword="null"/> for an aggregate over several scopes.</summary>
    public string? ScopeId { get; init; }

    /// <summary>Correlation id that also appears in all log entries of this run.</summary>
    public required string SessionId { get; init; }

    /// <summary>Start time (UTC).</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>Completion time (UTC).</summary>
    public required DateTimeOffset CompletedUtc { get; init; }

    /// <summary>Items uploaded (including tombstones and conflict copies).</summary>
    public int UploadedCount { get; init; }

    /// <summary>Items applied locally.</summary>
    public int DownloadedCount { get; init; }

    /// <summary>Items deleted locally or remotely.</summary>
    public int DeletedCount { get; init; }

    /// <summary>Items left unchanged on purpose (e.g. remote changes in upload-only mode).</summary>
    public int SkippedCount { get; init; }

    /// <summary>Unresolved conflicts.</summary>
    public int ConflictCount => Conflicts.Count;

    /// <summary>Bytes uploaded and downloaded.</summary>
    public long TransferredBytes { get; init; }

    /// <summary>Unresolved conflicts, for manual resolution.</summary>
    public IReadOnlyList<SyncConflict> Conflicts { get; init; } = [];

    /// <summary>Failures; affected items stay pending for the next run.</summary>
    public IReadOnlyList<SyncFailure> Failures { get; init; } = [];

    /// <summary>Per-operation results.</summary>
    public IReadOnlyList<SyncOperationResult> Operations { get; init; } = [];

    /// <summary>Per-scope results of an aggregate run (empty for a single scope).</summary>
    public IReadOnlyList<SyncResult> ScopeResults { get; init; } = [];

    /// <summary>Duration of the run.</summary>
    public TimeSpan Duration => CompletedUtc - StartedUtc;
}

/// <summary>Synchronization state of one scope.</summary>
/// <param name="ScopeId">Scope id (usually the data source id).</param>
/// <param name="ProviderId">Destination provider.</param>
/// <param name="Mode">Synchronization mode.</param>
/// <param name="LastSuccessUtc">Last successful run.</param>
/// <param name="LastAttemptUtc">Last run of any outcome.</param>
/// <param name="LastError">Last error message, cleared after a successful run.</param>
/// <param name="PendingConflicts">Conflicts waiting for manual resolution.</param>
public sealed record SyncScopeStatus(
    string ScopeId,
    string ProviderId,
    SyncMode Mode,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastAttemptUtc,
    string? LastError,
    int PendingConflicts);

/// <summary>Overall synchronization state.</summary>
/// <param name="Activity">What the service is doing now.</param>
/// <param name="Scopes">Per-scope state.</param>
/// <param name="Progress">Progress of the running synchronization, if any.</param>
public sealed record SyncStatus(SyncActivity Activity, IReadOnlyList<SyncScopeStatus> Scopes, SyncProgress? Progress)
{
    /// <summary>Most recent successful run of any scope.</summary>
    public DateTimeOffset? LastSuccessUtc => Scopes.Max(s => s.LastSuccessUtc);
}
