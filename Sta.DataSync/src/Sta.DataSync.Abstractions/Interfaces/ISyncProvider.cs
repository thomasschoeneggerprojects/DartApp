namespace Sta.DataSync;

/// <summary>
/// Defines where data is stored: a simple store of immutable-by-convention blobs addressed by logical paths below the
/// provider's namespace root. The engine builds manifests, versioning and conflict detection on top, so providers only
/// implement authentication, API communication, pagination, rate limits and error mapping.
/// </summary>
/// <remarks>
/// Errors must be mapped to <see cref="SyncException"/> types: <see cref="SyncTransientException"/> for retryable
/// failures, <see cref="SyncAuthenticationException"/> for missing or revoked credentials,
/// <see cref="SyncConcurrencyException"/> for failed preconditions and <see cref="SyncItemNotFoundException"/>.
/// </remarks>
public interface ISyncProvider
{
    /// <summary>Stable provider id, e.g. <c>google-drive</c>.</summary>
    string ProviderId { get; }

    /// <summary>Optional features; the engine refuses modes that need missing features.</summary>
    SyncProviderCapabilities Capabilities { get; }

    /// <summary>Lists all items whose path starts with <paramref name="pathPrefix"/> (all pages).</summary>
    Task<IReadOnlyList<RemoteSyncItem>> ListItemsAsync(string pathPrefix, CancellationToken cancellationToken = default);

    /// <summary>Gets an item by path, or <see langword="null"/> when it does not exist.</summary>
    Task<RemoteSyncItem?> GetItemAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Opens the content of an item for streaming reads.</summary>
    Task<Stream> DownloadAsync(RemoteSyncItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores content at <see cref="RemoteUpload.Path"/>, replacing an existing item at that path.
    /// <see cref="RemoteUpload.ExpectedVersion"/> is only allowed with <see cref="SyncProviderCapabilities.ConditionalWrites"/>.
    /// </summary>
    Task<RemoteSyncItem> UploadAsync(RemoteUpload upload, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Deletes an item. Deleting a missing item succeeds.</summary>
    Task DeleteAsync(RemoteSyncItem item, string? expectedVersion = null, CancellationToken cancellationToken = default);
}

/// <summary>Optional provider capability: incremental remote change detection.</summary>
public interface IRemoteChangeFeed
{
    /// <summary>Gets a token that marks "now".</summary>
    Task<string> GetStartTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Reports whether items below <paramref name="pathPrefix"/> changed since <paramref name="token"/>.</summary>
    /// <exception cref="SyncConcurrencyException">The token expired; the caller starts over with a full listing.</exception>
    Task<RemoteChangeSet> GetChangesAsync(string token, string pathPrefix, CancellationToken cancellationToken = default);
}

/// <summary>Result of a change-feed query.</summary>
/// <param name="HasChanges"><see langword="true"/> when at least one item below the prefix changed.</param>
/// <param name="NextToken">Token for the next query.</param>
public sealed record RemoteChangeSet(bool HasChanges, string NextToken);

/// <summary>Decides conflicts for <see cref="ConflictResolutionStrategy.Custom"/>.</summary>
public interface IConflictResolver
{
    /// <summary>Returns the decision for <paramref name="conflict"/>.</summary>
    Task<ConflictResolution> ResolveAsync(SyncConflict conflict, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extension point for transforming stored content, e.g. future end-to-end encryption. Independent of the provider.
/// No implementation ships with version 1, so content is stored as-is unless the application registers one.
/// </summary>
public interface ISyncContentTransform
{
    /// <summary>Id written to the manifest, so readers know which transform to reverse.</summary>
    string TransformId { get; }

    /// <summary>Wraps plain content for upload.</summary>
    Task<Stream> WrapForUploadAsync(Stream plainContent, CancellationToken cancellationToken = default);

    /// <summary>Unwraps downloaded content.</summary>
    Task<Stream> UnwrapAfterDownloadAsync(Stream storedContent, CancellationToken cancellationToken = default);
}

/// <summary>Reports network availability (implemented by platform integrations).</summary>
public interface IConnectivityMonitor
{
    /// <summary><see langword="true"/> when the internet is reachable.</summary>
    bool IsConnected { get; }

    /// <summary><see langword="true"/> when the connection is not metered (Wi-Fi or Ethernet).</summary>
    bool IsUnmetered { get; }

    /// <summary>Raised when connectivity changes. May be raised on any thread.</summary>
    event EventHandler? ConnectivityChanged;
}
