namespace Sta.DataSync;

/// <summary>Direction of a synchronization scope.</summary>
public enum SyncMode
{
    /// <summary>
    /// Local changes go to the destination; remote changes are never applied locally.
    /// Local deletions become remote tombstones when <see cref="DeletionPolicy.Propagate"/> is configured.
    /// </summary>
    UploadOnly,

    /// <summary>
    /// Remote changes are applied locally; local changes are never uploaded. The local side mirrors the destination:
    /// locally modified or deleted items are restored from the destination, and remote tombstones delete local items
    /// when <see cref="DeletionPolicy.Propagate"/> is configured.
    /// </summary>
    DownloadOnly,

    /// <summary>Changes flow both ways. Items changed on both sides since the last common state are conflicts.</summary>
    Bidirectional,
}

/// <summary>Whether deletions are propagated. Missing items without a tombstone are never treated as deletions.</summary>
public enum DeletionPolicy
{
    /// <summary>Deletions with reliable evidence (local baseline or remote tombstone) are propagated.</summary>
    Propagate,

    /// <summary>Deletions are never propagated; the item is kept on the other side.</summary>
    Ignore,
}

/// <summary>How conflicts (an item changed independently on both sides) are resolved.</summary>
public enum ConflictResolutionStrategy
{
    /// <summary>The local version replaces the remote version.</summary>
    KeepLocal,

    /// <summary>The remote version replaces the local version.</summary>
    KeepRemote,

    /// <summary>The remote version is kept under the original id; the local version is saved as a conflict copy.</summary>
    KeepBoth,

    /// <summary>
    /// The version with the newer modification time wins. Only safe when device clocks are reliable;
    /// ties and missing timestamps fall back to <see cref="KeepBoth"/>.
    /// </summary>
    LatestWins,

    /// <summary>Nothing is changed; the conflict is reported and kept until the application resolves it.</summary>
    Manual,

    /// <summary>A registered <see cref="IConflictResolver"/> decides.</summary>
    Custom,
}

/// <summary>Decision for one conflict.</summary>
public enum ConflictResolutionKind
{
    /// <summary>Upload the local version.</summary>
    KeepLocal,

    /// <summary>Apply the remote version locally.</summary>
    KeepRemote,

    /// <summary>Keep the remote version and store the local one as a conflict copy.</summary>
    KeepBoth,

    /// <summary>Leave both versions untouched and report the conflict again later.</summary>
    Defer,
}

/// <summary>Kind of a planned synchronization operation.</summary>
public enum SyncOperationKind
{
    /// <summary>Upload local content.</summary>
    Upload,

    /// <summary>Apply remote content locally.</summary>
    Download,

    /// <summary>Delete the local item (remote tombstone).</summary>
    DeleteLocal,

    /// <summary>Write a remote tombstone (local deletion).</summary>
    DeleteRemote,

    /// <summary>Both sides already agree; only the baseline is updated.</summary>
    Adopt,

    /// <summary>Upload the local version under a conflict-copy id (part of <see cref="ConflictResolutionKind.KeepBoth"/>).</summary>
    UploadConflictCopy,

    /// <summary>Unresolved conflict; nothing is changed.</summary>
    Conflict,
}

/// <summary>Outcome of a synchronization run.</summary>
public enum SyncResultStatus
{
    /// <summary>All planned operations succeeded.</summary>
    Succeeded,

    /// <summary>Nothing needed to be transferred.</summary>
    NoChanges,

    /// <summary>Some operations failed or conflicts are unresolved; successful ones were committed.</summary>
    PartiallySucceeded,

    /// <summary>The run failed; no metadata was committed for failed operations.</summary>
    Failed,

    /// <summary>The run was canceled; completed operations were committed.</summary>
    Canceled,

    /// <summary>The destination requires (re)authentication. Automatic retries stop until sign-in.</summary>
    AuthenticationRequired,
}

/// <summary>Current activity of the synchronization service.</summary>
public enum SyncActivity
{
    /// <summary>No synchronization is running.</summary>
    Idle,

    /// <summary>A synchronization is running.</summary>
    Synchronizing,

    /// <summary>Automatic synchronization is paused.</summary>
    Paused,

    /// <summary>No network connection; changes are kept locally.</summary>
    Offline,
}

/// <summary>Kind of a change applied to a data source.</summary>
public enum SyncChangeKind
{
    /// <summary>Create or replace the item with the supplied content.</summary>
    Write,

    /// <summary>Delete the item.</summary>
    Delete,
}

/// <summary>Optional destination features. The engine checks them before it plans or executes operations.</summary>
[Flags]
public enum SyncProviderCapabilities
{
    /// <summary>Only the basic blob operations.</summary>
    None = 0,

    /// <summary>Uploads and deletes honor an expected version atomically (compare-and-swap).</summary>
    ConditionalWrites = 1,

    /// <summary>Server-assigned creation timestamps give a consistent order of created files.</summary>
    ServerCreationOrder = 2,

    /// <summary>The provider implements <see cref="IRemoteChangeFeed"/>.</summary>
    ChangeFeed = 4,

    /// <summary>The provider reports SHA-256 content hashes for stored items.</summary>
    ContentHashes = 8,

    /// <summary>Large uploads resume after interruptions.</summary>
    ResumableUploads = 16,
}

/// <summary>Optional data source features.</summary>
[Flags]
public enum SyncDataSourceCapabilities
{
    /// <summary>The source can only be read (upload only).</summary>
    ReadOnly = 0,

    /// <summary>The source can create and replace items.</summary>
    Write = 1,

    /// <summary>The source can delete items.</summary>
    Delete = 2,

    /// <summary>
    /// The source can create items with new ids (needed to keep conflict copies). Without it,
    /// <see cref="ConflictResolutionStrategy.KeepBoth"/> leaves conflicts for a manual decision.
    /// </summary>
    CreateItems = 4,
}
