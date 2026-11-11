using Sta.DataSync.Detection;
using Sta.DataSync.Manifest;

namespace Sta.DataSync.Planning;

/// <summary>
/// Deterministic three-way comparison of local state, remote manifest and the last common baseline.
/// A side "changed" when its content hash (or deletion) differs from the baseline; timestamps are never used here.
/// </summary>
/// <remarks>
/// Deletion evidence: a local deletion needs a baseline (the item existed at the last sync); a remote deletion needs a
/// tombstone. A remote item that is simply missing is never treated as deleted: with a baseline it is re-uploaded
/// (repair), otherwise it is unknown remotely.
/// </remarks>
internal static class SyncPlanner
{
    private const string DeletedMarker = "<deleted>";

    public static SyncPlan Plan(PlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var operations = new List<PlannedOperation>();
        var skipped = new List<SkippedItem>();

        IEnumerable<string> ids = input.Local.Keys
            .Concat(input.Remote.Keys)
            .Concat(input.Bases.Values.Where(b => b.HasBaseline).Select(b => b.ItemId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (string id in ids)
        {
            var context = new ItemContext(
                id,
                input.Local.GetValueOrDefault(id),
                input.Remote.GetValueOrDefault(id),
                input.Bases.TryGetValue(id, out ItemSyncState? state) && state.HasBaseline ? state : null,
                input.Mode,
                input.DeletionPolicy);
            Decide(context, operations, skipped);
        }

        return new SyncPlan(operations, skipped);
    }

    private static void Decide(ItemContext c, List<PlannedOperation> operations, List<SkippedItem> skipped)
    {
        PlannedOperation? operation = c.Base is null ? DecideWithoutBase(c, skipped) : DecideWithBase(c, skipped);
        if (operation is not null) operations.Add(operation with { Local = c.Local, Remote = c.Remote, Base = c.Base });
    }

    private static PlannedOperation? DecideWithoutBase(ItemContext c, List<SkippedItem> skipped)
    {
        if (c.Local is not null && c.RemoteValue is not null && c.RemoteValue != DeletedMarker)
        {
            return c.LocalValue == c.RemoteValue
                ? c.Op(SyncOperationKind.Adopt, "Both sides have the same content.")
                : c.Op(SyncOperationKind.Conflict, "Both sides created the item with different content.");
        }

        if (c.Local is not null && c.RemoteValue == DeletedMarker)
        {
            return c.Mode switch
            {
                SyncMode.Bidirectional => c.Op(SyncOperationKind.Conflict, "The item exists locally but was deleted on another device."),
                SyncMode.UploadOnly => c.Op(SyncOperationKind.Upload, "New local item (upload-only)."),
                _ => c.PropagatesDeletions
                    ? c.Op(SyncOperationKind.DeleteLocal, "Deleted remotely (download-only mirror).")
                    : c.Skip(skipped, "Remote deletion is not propagated."),
            };
        }

        if (c.Local is not null)
        {
            return c.Uploads ? c.Op(SyncOperationKind.Upload, "New local item.") : c.Skip(skipped, "Local-only item is not uploaded in download-only mode.");
        }

        if (c.RemoteValue == DeletedMarker) return c.Op(SyncOperationKind.Adopt, "Tombstone of an item unknown locally.");
        return c.Downloads ? c.Op(SyncOperationKind.Download, "New remote item.") : c.Skip(skipped, "Remote item is not downloaded in upload-only mode.");
    }

    private static PlannedOperation? DecideWithBase(ItemContext c, List<SkippedItem> skipped)
    {
        if (c.RemoteValue is null) return DecideRemoteMissing(c, skipped);

        bool localChanged = c.LocalValue != c.BaseValue;
        bool remoteChanged = c.RemoteValue != c.BaseValue;

        if (!localChanged && !remoteChanged)
        {
            return c.Remote!.Version != c.Base!.BaseVersion ? c.Op(SyncOperationKind.Adopt, "Remote version advanced without content change.") : null;
        }

        if (localChanged && remoteChanged)
        {
            return c.LocalValue == c.RemoteValue
                ? c.Op(SyncOperationKind.Adopt, "Both sides made the same change.")
                : c.Op(SyncOperationKind.Conflict, "Changed on this device and on another device since the last synchronization.");
        }

        return localChanged ? DecideLocalChange(c, skipped) : DecideRemoteChange(c, skipped);
    }

    private static PlannedOperation? DecideLocalChange(ItemContext c, List<SkippedItem> skipped)
    {
        bool deletedLocally = c.Local is null;
        if (c.Uploads)
        {
            if (!deletedLocally) return c.Op(SyncOperationKind.Upload, "Changed locally.");
            return c.PropagatesDeletions ? c.Op(SyncOperationKind.DeleteRemote, "Deleted locally.") : c.Skip(skipped, "Local deletion is not propagated.");
        }

        // Download-only: the local side mirrors the destination.
        if (c.RemoteValue == DeletedMarker)
        {
            return c.PropagatesDeletions ? c.Op(SyncOperationKind.DeleteLocal, "Deleted remotely (download-only mirror).") : c.Skip(skipped, "Remote deletion is not propagated.");
        }

        return c.Op(SyncOperationKind.Download, "Local change replaced by the remote version (download-only mirror).");
    }

    private static PlannedOperation? DecideRemoteChange(ItemContext c, List<SkippedItem> skipped)
    {
        if (!c.Downloads) return c.Skip(skipped, "Remote change is not applied in upload-only mode.");
        if (c.RemoteValue != DeletedMarker) return c.Op(SyncOperationKind.Download, "Changed on another device.");
        return c.PropagatesDeletions ? c.Op(SyncOperationKind.DeleteLocal, "Deleted on another device.") : c.Skip(skipped, "Remote deletion is not propagated.");
    }

    private static PlannedOperation? DecideRemoteMissing(ItemContext c, List<SkippedItem> skipped)
    {
        if (c.Local is null) return c.Op(SyncOperationKind.Adopt, "Gone on both sides.");
        return c.Uploads
            ? c.Op(SyncOperationKind.Upload, "Missing remotely without a tombstone; restoring it.")
            : c.Skip(skipped, "Missing remotely without a tombstone; kept locally.");
    }

    private sealed record ItemContext(
        string Id,
        LocalItem? Local,
        SyncManifestEntry? Remote,
        ItemSyncState? Base,
        SyncMode Mode,
        DeletionPolicy Policy)
    {
        public string LocalValue => Local?.Hash ?? DeletedMarker;

        public string? RemoteValue => Remote is null ? null : Remote.Deleted ? DeletedMarker : Remote.Hash;

        public string? BaseValue => Base is null ? null : Base.BaseDeleted ? DeletedMarker : Base.BaseHash;

        public bool Uploads => Mode != SyncMode.DownloadOnly;

        public bool Downloads => Mode != SyncMode.UploadOnly;

        public bool PropagatesDeletions => Policy == DeletionPolicy.Propagate;

        public PlannedOperation Op(SyncOperationKind kind, string reason) => new(Id, kind) { Reason = reason };

        public PlannedOperation? Skip(List<SkippedItem> skipped, string reason)
        {
            skipped.Add(new SkippedItem(Id, reason));
            return null;
        }
    }
}
