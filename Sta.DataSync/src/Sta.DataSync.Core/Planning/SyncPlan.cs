using Sta.DataSync.Detection;
using Sta.DataSync.Manifest;

namespace Sta.DataSync.Planning;

/// <summary>One planned step. Immutable; operations of the same <see cref="GroupId"/> run in order.</summary>
internal sealed record PlannedOperation(string ItemId, SyncOperationKind Kind)
{
    /// <summary>Current local version (null when absent locally).</summary>
    public LocalItem? Local { get; init; }

    /// <summary>Current remote manifest entry (null when absent remotely).</summary>
    public SyncManifestEntry? Remote { get; init; }

    /// <summary>Last common state.</summary>
    public ItemSyncState? Base { get; init; }

    /// <summary>For conflict copies: write the copy locally too.</summary>
    public bool WritesLocalCopy { get; init; }

    /// <summary>Why this operation (or conflict) was planned.</summary>
    public string? Reason { get; init; }

    /// <summary>Operations with the same group id run sequentially (e.g. conflict copy before the overwrite).</summary>
    public string GroupId { get; init; } = ItemId;

    /// <summary><see langword="true"/> for operations that change the remote manifest.</summary>
    public bool ChangesRemote => Kind is SyncOperationKind.Upload or SyncOperationKind.DeleteRemote
        || (Kind == SyncOperationKind.UploadConflictCopy && Remote is not null);
}

/// <summary>An item left unchanged on purpose.</summary>
internal sealed record SkippedItem(string ItemId, string Reason);

/// <summary>Immutable synchronization plan.</summary>
internal sealed record SyncPlan(IReadOnlyList<PlannedOperation> Operations, IReadOnlyList<SkippedItem> Skipped)
{
    public static SyncPlan Empty { get; } = new([], []);

    /// <summary><see langword="true"/> when something has to be transferred or written.</summary>
    public bool HasTransfers => Operations.Any(o => o.Kind is not (SyncOperationKind.Adopt or SyncOperationKind.Conflict));
}

/// <summary>Everything the planner compares.</summary>
internal sealed record PlanningInput(
    IReadOnlyDictionary<string, LocalItem> Local,
    IReadOnlyDictionary<string, SyncManifestEntry> Remote,
    IReadOnlyDictionary<string, ItemSyncState> Bases,
    SyncMode Mode,
    DeletionPolicy DeletionPolicy);
