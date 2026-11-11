using System.Globalization;
using Sta.DataSync.Detection;
using Sta.DataSync.Planning;

namespace Sta.DataSync.Conflicts;

/// <summary>Result of conflict resolution: the executable plan and the conflicts that stay open.</summary>
internal sealed record ResolvedPlan(SyncPlan Plan, IReadOnlyList<SyncConflict> Unresolved, IReadOnlyList<string> ResolvedItemIds);

/// <summary>
/// Turns <see cref="SyncOperationKind.Conflict"/> operations into concrete operations according to the application's
/// stored decision, the scope's strategy or a custom <see cref="IConflictResolver"/>. Contains no application-specific
/// merge rules. A decision is only applied while both versions are still the ones it was made for.
/// </summary>
internal sealed class ConflictResolutionService(
    string scopeId,
    SyncMode mode,
    ConflictResolutionStrategy strategy,
    IConflictResolver? customResolver,
    string deviceId,
    TimeProvider time,
    SyncDataSourceCapabilities sourceCapabilities = SyncDataSourceCapabilities.Write | SyncDataSourceCapabilities.Delete | SyncDataSourceCapabilities.CreateItems)
{
    public async Task<ResolvedPlan> ResolveAsync(
        SyncPlan plan,
        IReadOnlyDictionary<string, SyncConflict> stored,
        IReadOnlyDictionary<string, ConflictResolution> decisions,
        IReadOnlySet<string> existingIds,
        CancellationToken cancellationToken)
    {
        var operations = new List<PlannedOperation>(plan.Operations.Count);
        var unresolved = new List<SyncConflict>();
        var resolved = new List<string>();
        var usedIds = new HashSet<string>(existingIds, StringComparer.Ordinal);

        foreach (PlannedOperation operation in plan.Operations)
        {
            if (operation.Kind != SyncOperationKind.Conflict)
            {
                operations.Add(operation);
                continue;
            }

            SyncConflict conflict = Describe(operation, stored.GetValueOrDefault(operation.ItemId));
            ConflictResolution decision = await DecideAsync(conflict, stored, decisions, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<PlannedOperation>? concrete = Translate(operation, decision, usedIds);
            if (concrete is null)
            {
                unresolved.Add(conflict);
                continue;
            }

            operations.AddRange(concrete);
            resolved.Add(operation.ItemId);
        }

        foreach (string id in stored.Keys)
        {
            if (!plan.Operations.Any(o => o.Kind == SyncOperationKind.Conflict && o.ItemId == id)) resolved.Add(id);
        }

        return new ResolvedPlan(plan with { Operations = operations }, unresolved, resolved.Distinct(StringComparer.Ordinal).ToList());
    }

    private async Task<ConflictResolution> DecideAsync(
        SyncConflict conflict,
        IReadOnlyDictionary<string, SyncConflict> stored,
        IReadOnlyDictionary<string, ConflictResolution> decisions,
        CancellationToken cancellationToken)
    {
        if (decisions.TryGetValue(conflict.ItemId, out ConflictResolution? decision)
            && stored.TryGetValue(conflict.ItemId, out SyncConflict? previous)
            && previous.Local.ContentHash == conflict.Local.ContentHash
            && previous.Remote.ContentHash == conflict.Remote.ContentHash)
        {
            return decision;
        }

        return strategy switch
        {
            ConflictResolutionStrategy.KeepLocal => ConflictResolution.KeepLocal,
            ConflictResolutionStrategy.KeepRemote => ConflictResolution.KeepRemote,
            ConflictResolutionStrategy.KeepBoth => ConflictResolution.KeepBoth,
            ConflictResolutionStrategy.LatestWins => LatestWins(conflict),
            ConflictResolutionStrategy.Custom when customResolver is not null
                => await customResolver.ResolveAsync(conflict, cancellationToken).ConfigureAwait(false) ?? ConflictResolution.Defer,
            _ => ConflictResolution.Defer,
        };
    }

    /// <summary>Newer timestamp wins; equal or unknown timestamps keep both versions.</summary>
    private static ConflictResolution LatestWins(SyncConflict conflict)
        => (conflict.Local.ModifiedUtc, conflict.Remote.ModifiedUtc) switch
        {
            ({ } local, { } remote) when local > remote => ConflictResolution.KeepLocal,
            ({ } local, { } remote) when remote > local => ConflictResolution.KeepRemote,
            _ => ConflictResolution.KeepBoth,
        };

    /// <summary>Concrete operations for a decision, or <see langword="null"/> when it stays open (deferred or not allowed in this mode).</summary>
    private IReadOnlyList<PlannedOperation>? Translate(PlannedOperation conflict, ConflictResolution decision, HashSet<string> usedIds)
    {
        bool uploads = mode != SyncMode.DownloadOnly;
        bool downloads = mode != SyncMode.UploadOnly;
        bool localExists = conflict.Local is not null;
        bool remoteExists = conflict.Remote is { Deleted: false };
        string reason = $"Conflict resolved: {decision.Kind}.";

        switch (decision.Kind)
        {
            case ConflictResolutionKind.KeepLocal when uploads:
                return [conflict with { Kind = localExists ? SyncOperationKind.Upload : SyncOperationKind.DeleteRemote, Reason = reason }];
            case ConflictResolutionKind.KeepRemote when downloads:
                return [conflict with { Kind = remoteExists ? SyncOperationKind.Download : SyncOperationKind.DeleteLocal, Reason = reason }];
            case ConflictResolutionKind.KeepBoth when !localExists:
                return Translate(conflict, ConflictResolution.KeepRemote, usedIds);
            case ConflictResolutionKind.KeepBoth when !remoteExists:
                return Translate(conflict, ConflictResolution.KeepLocal, usedIds);
            case ConflictResolutionKind.KeepBoth when downloads && !sourceCapabilities.HasFlag(SyncDataSourceCapabilities.CreateItems):
                // The source cannot hold a second item (e.g. a database snapshot): leave it to the application.
                return null;
            case ConflictResolutionKind.KeepBoth:
                string copyId = decision.ConflictCopyId is { } requested && SyncPath.IsValid(requested) && usedIds.Add(requested)
                    ? requested
                    : ConflictCopyNames.Create(conflict.ItemId, deviceId, time.GetUtcNow(), usedIds);
                // The copy comes first: a failure stops the group, so the original is never replaced without its copy.
                return uploads
                    ? [
                        new PlannedOperation(copyId, SyncOperationKind.UploadConflictCopy)
                        {
                            Remote = conflict.Remote,
                            WritesLocalCopy = downloads,
                            GroupId = conflict.ItemId,
                            Reason = $"Remote version of '{conflict.ItemId}' kept as conflict copy.",
                        },
                        conflict with { Kind = SyncOperationKind.Upload, Reason = reason },
                    ]
                    : [
                        new PlannedOperation(copyId, SyncOperationKind.UploadConflictCopy)
                        {
                            Local = conflict.Local,
                            WritesLocalCopy = true,
                            GroupId = conflict.ItemId,
                            Reason = $"Local version of '{conflict.ItemId}' kept as conflict copy.",
                        },
                        conflict with { Kind = SyncOperationKind.Download, Reason = reason },
                    ];
            default:
                return null;
        }
    }

    private SyncConflict Describe(PlannedOperation operation, SyncConflict? previous)
    {
        var local = new SyncConflictVersion(operation.Local?.Hash, operation.Local?.Length ?? 0, operation.Local?.ModifiedUtc, deviceId);
        var remote = new SyncConflictVersion(
            operation.Remote is { Deleted: false } r ? r.Hash : null,
            operation.Remote?.Size ?? 0,
            operation.Remote?.ModifiedUtc,
            operation.Remote?.ModifiedBy);
        bool same = previous is not null && previous.Local.ContentHash == local.ContentHash && previous.Remote.ContentHash == remote.ContentHash;
        return new SyncConflict(scopeId, operation.ItemId, local, remote, operation.Base?.BaseHash, same ? previous!.DetectedUtc : time.GetUtcNow());
    }
}

/// <summary>Names for conflict copies: <c>dir/name (conflict 2026-01-31 1205 device).ext</c>, unique among known ids.</summary>
internal static class ConflictCopyNames
{
    public static string Create(string itemId, string deviceId, DateTimeOffset now, HashSet<string> usedIds)
    {
        int slash = itemId.LastIndexOf('/');
        string directory = slash >= 0 ? itemId[..(slash + 1)] : string.Empty;
        string name = itemId[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        (string stem, string extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        string stamp = now.UtcDateTime.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        string device = deviceId.Length > 6 ? deviceId[..6] : deviceId;

        for (int counter = 1; ; counter++)
        {
            string suffix = counter == 1 ? string.Empty : " " + counter.ToString(CultureInfo.InvariantCulture);
            string candidate = $"{directory}{stem} (conflict {stamp} {device}{suffix}){extension}";
            if (SyncPath.IsValid(candidate) && usedIds.Add(candidate)) return candidate;
        }
    }
}
