using Sta.DataSync.Detection;

namespace Sta.DataSync.Engine;

/// <summary>Effective settings of a scope (definition values or defaults).</summary>
internal sealed record ScopeSettings(SyncMode Mode, ConflictResolutionStrategy Strategy, DeletionPolicy DeletionPolicy)
{
    public bool Uploads => Mode != SyncMode.DownloadOnly;
}

/// <summary>Journal payload of the commit phase: enough to finish the commit after a crash.</summary>
internal sealed record CommitJournal(string ManifestPath, string CommitId, SyncStateCommit StateCommit);

/// <summary>Baseline of an item before this device's last commit (null: the item had no state).</summary>
internal sealed record RollbackEntry(string ItemId, ItemSyncState? Previous);

/// <summary>Journal phases.</summary>
internal static class JournalPhase
{
    public const string Executing = "Executing";
    public const string Committing = "Committing";
}

/// <summary>Consistent local view for one session (a snapshot or the live source).</summary>
internal sealed class LocalView : IAsyncDisposable
{
    private readonly ISyncSnapshot? _snapshot;

    private LocalView(IReadOnlyList<SyncItem> items, LocalReader reader, ISyncSnapshot? snapshot)
    {
        Items = items;
        Reader = reader;
        _snapshot = snapshot;
    }

    public IReadOnlyList<SyncItem> Items { get; }

    public LocalReader Reader { get; }

    public static async Task<LocalView> OpenAsync(ISyncDataSource source, CancellationToken cancellationToken)
    {
        if (source is ISyncSnapshotSource snapshots)
        {
            ISyncSnapshot snapshot = await snapshots.CreateSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return new LocalView(snapshot.Items, snapshot.OpenReadAsync, snapshot);
        }

        IReadOnlyList<SyncItem> items = await source.GetItemsAsync(cancellationToken).ConfigureAwait(false);
        return new LocalView(items, source.OpenReadAsync, null);
    }

    public ValueTask DisposeAsync() => _snapshot?.DisposeAsync() ?? ValueTask.CompletedTask;
}
