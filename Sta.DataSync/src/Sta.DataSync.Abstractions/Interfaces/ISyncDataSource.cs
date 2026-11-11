namespace Sta.DataSync;

/// <summary>
/// Defines what is synchronized. The engine never knows how the application stores its data; it only enumerates items,
/// reads their content and asks the source to apply changes.
/// </summary>
public interface ISyncDataSource
{
    /// <summary>Stable id of the source (also the default scope id).</summary>
    string SourceId { get; }

    /// <summary>Which changes the source can apply.</summary>
    SyncDataSourceCapabilities Capabilities { get; }

    /// <summary>Lists the current items. Ids must be valid <see cref="SyncPath"/> values.</summary>
    Task<IReadOnlyList<SyncItem>> GetItemsAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens an item for streaming reads.</summary>
    /// <exception cref="SyncItemNotFoundException">The item no longer exists.</exception>
    Task<Stream> OpenReadAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a change atomically: a write must never leave a partially written item, and must not replace the item when
    /// the content stream fails its hash check or the current version differs from
    /// <see cref="SyncChange.ExpectedCurrentFingerprint"/> (<see cref="SyncConcurrencyException"/>).
    /// </summary>
    Task ApplyChangeAsync(SyncChange change, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability: the source can provide a consistent, read-only view for one synchronization session
/// (e.g. a SQLite backup). The engine reads only from the snapshot while it is open.
/// </summary>
public interface ISyncSnapshotSource
{
    /// <summary>Creates a snapshot; dispose it to release temporary resources.</summary>
    Task<ISyncSnapshot> CreateSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>A consistent read-only view of a data source.</summary>
public interface ISyncSnapshot : IAsyncDisposable
{
    /// <summary>Items in the snapshot.</summary>
    IReadOnlyList<SyncItem> Items { get; }

    /// <summary>Opens an item of the snapshot for streaming reads.</summary>
    Task<Stream> OpenReadAsync(string itemId, CancellationToken cancellationToken = default);
}

/// <summary>Optional capability: the source reports local changes, so synchronization can follow them.</summary>
public interface ISyncChangeTracking
{
    /// <summary>Raised after local data changed. May be raised on any thread.</summary>
    event EventHandler<SyncSourceChangedEventArgs>? Changed;
}
