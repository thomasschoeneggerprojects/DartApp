namespace Sta.DataSync;

/// <summary>Data of <see cref="IDataSyncService.StatusChanged"/>.</summary>
/// <param name="status">The new status.</param>
public sealed class SyncStatusChangedEventArgs(SyncStatus status) : EventArgs
{
    /// <summary>The new status.</summary>
    public SyncStatus Status { get; } = status;
}

/// <summary>Data of <see cref="IDataSyncService.ProgressChanged"/>.</summary>
/// <param name="progress">Current progress.</param>
public sealed class SyncProgressEventArgs(SyncProgress progress) : EventArgs
{
    /// <summary>Current progress.</summary>
    public SyncProgress Progress { get; } = progress;
}

/// <summary>Data of <see cref="IDataSyncService.SyncCompleted"/>.</summary>
/// <param name="result">Result of the run.</param>
public sealed class SyncCompletedEventArgs(SyncResult result) : EventArgs
{
    /// <summary>Result of the run.</summary>
    public SyncResult Result { get; } = result;
}

/// <summary>Data of <see cref="ISyncChangeTracking.Changed"/>.</summary>
/// <param name="sourceId">The source that changed.</param>
/// <param name="itemId">The changed item, if known.</param>
public sealed class SyncSourceChangedEventArgs(string sourceId, string? itemId = null) : EventArgs
{
    /// <summary>The source that changed.</summary>
    public string SourceId { get; } = sourceId;

    /// <summary>The changed item, if known.</summary>
    public string? ItemId { get; } = itemId;
}
