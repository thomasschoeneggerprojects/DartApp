using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sta.DataSync.Detection;
using Sta.DataSync.Manifest;
using Sta.DataSync.Planning;
using Sta.DataSync.Validation;

namespace Sta.DataSync.Execution;

/// <summary>Outcome of one executed operation, including the metadata to commit when it succeeded.</summary>
internal sealed record OperationOutcome(PlannedOperation Operation, bool Succeeded, long Bytes = 0, string? Error = null, bool Transient = false)
{
    /// <summary>New or changed manifest entry (remote-side operations).</summary>
    public SyncManifestEntry? RemoteEntry { get; init; }

    /// <summary>New baseline for the item, or <see langword="null"/> with <see cref="RemovesState"/>.</summary>
    public ItemSyncState? Baseline { get; init; }

    /// <summary>The item's state is removed (gone on both sides).</summary>
    public bool RemovesState { get; init; }
}

/// <summary>Result of executing a plan.</summary>
internal sealed record ExecutionReport(IReadOnlyList<OperationOutcome> Outcomes, bool Canceled, SyncAuthenticationException? AuthenticationFailure);

/// <summary>Dependencies of an execution.</summary>
internal sealed record ExecutionContext(
    string ScopeId,
    ISyncDataSource Source,
    ISyncProvider Provider,
    LocalReader ReadLocal,
    RemoteLayout Layout,
    IReadOnlyDictionary<string, RemoteSyncItem> RemoteObjects,
    ISyncContentTransform? Transform,
    RetryPolicy Retry,
    string DeviceId,
    int MaxParallelism,
    TimeProvider Time,
    ILogger Logger,
    Action<int, long, string?> ReportProgress);

/// <summary>
/// Executes a plan with bounded parallelism. Operations of one group run in order. Uploads stream through a hashing
/// wrapper and are rejected when the content changed after detection; downloads reach the source through a verifying
/// stream, so corrupt or incomplete content never replaces local data. Nothing here touches the state store: the engine
/// commits metadata only for successful outcomes.
/// </summary>
internal sealed partial class SyncExecutor(ExecutionContext context)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<long>>> _objectUploads = new(StringComparer.Ordinal);
    private int _completed;
    private long _bytes;

    public async Task<ExecutionReport> ExecuteAsync(SyncPlan plan, CancellationToken cancellationToken)
    {
        var outcomes = new ConcurrentBag<OperationOutcome>();
        SyncAuthenticationException? authenticationFailure = null;
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var groups = plan.Operations.GroupBy(o => o.GroupId, StringComparer.Ordinal).Select(g => g.ToList()).ToList();
        int total = plan.Operations.Count;

        try
        {
            await Parallel.ForEachAsync(
                groups,
                new ParallelOptions { MaxDegreeOfParallelism = context.MaxParallelism, CancellationToken = abort.Token },
                async (group, ct) =>
                {
                    foreach (PlannedOperation operation in group)
                    {
                        ct.ThrowIfCancellationRequested();
                        OperationOutcome outcome;
                        try
                        {
                            outcome = await RunAsync(operation, ct).ConfigureAwait(false);
                            outcomes.Add(outcome);
                        }
                        catch (SyncAuthenticationException ex)
                        {
                            authenticationFailure = ex;
                            await abort.CancelAsync().ConfigureAwait(false);
                            throw new OperationCanceledException(ex.Message, ex, abort.Token);
                        }

                        context.ReportProgress(Interlocked.Increment(ref _completed), Interlocked.Read(ref _bytes), operation.ItemId);

                        // Operations of a group depend on each other (conflict copy before overwrite): stop at the first failure.
                        if (!outcome.Succeeded) break;
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested)
        {
            // Canceled by the caller or stopped after an authentication failure; completed outcomes are kept.
        }

        bool canceled = cancellationToken.IsCancellationRequested;
        LogExecuted(context.Logger, context.ScopeId, outcomes.Count(o => o.Succeeded), total);
        return new ExecutionReport(outcomes.ToList(), canceled, authenticationFailure);
    }

    private async Task<OperationOutcome> RunAsync(PlannedOperation operation, CancellationToken cancellationToken)
    {
        string? invalid = SyncValidator.ValidateOperation(operation, context.Source.Capabilities);
        if (invalid is not null) return new OperationOutcome(operation, false, Error: invalid);

        try
        {
            return operation.Kind switch
            {
                SyncOperationKind.Upload => await UploadAsync(operation, cancellationToken).ConfigureAwait(false),
                SyncOperationKind.DeleteRemote => DeleteRemote(operation),
                SyncOperationKind.Download => await DownloadAsync(operation, cancellationToken).ConfigureAwait(false),
                SyncOperationKind.DeleteLocal => await DeleteLocalAsync(operation, cancellationToken).ConfigureAwait(false),
                SyncOperationKind.UploadConflictCopy => await ConflictCopyAsync(operation, cancellationToken).ConfigureAwait(false),
                SyncOperationKind.Adopt => Adopt(operation),
                _ => new OperationOutcome(operation, false, Error: "Unresolved conflict."),
            };
        }
        catch (SyncAuthenticationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SyncException or IOException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException)
        {
            bool transient = RetryPolicy.IsTransient(ex, cancellationToken);
            LogOperationFailed(context.Logger, context.ScopeId, operation.Kind, operation.ItemId, ex.Message);
            return new OperationOutcome(operation, false, Error: ex.Message, Transient: transient);
        }
    }

    private async Task<OperationOutcome> UploadAsync(PlannedOperation operation, CancellationToken cancellationToken)
    {
        LocalItem local = operation.Local!;
        LogTransfer(context.Logger, "Upload", operation.ItemId, local.Length);
        (long bytes, long length) = await EnsureObjectAsync(local, cancellationToken).ConfigureAwait(false);
        long version = NextVersion(operation);
        string objectName = RemoteLayout.ObjectName(local.Hash, context.Transform?.TransformId);
        var entry = new SyncManifestEntry
        {
            Id = operation.ItemId,
            Version = version,
            Hash = local.Hash,
            Size = length,
            ModifiedUtc = local.ModifiedUtc ?? context.Time.GetUtcNow(),
            ModifiedBy = context.DeviceId,
            Object = objectName,
            Transform = context.Transform?.TransformId,
        };
        return new OperationOutcome(operation, true, bytes)
        {
            RemoteEntry = entry,
            Baseline = Baseline(operation.ItemId, local.Hash, length, version, deleted: false, local),
        };
    }

    private OperationOutcome DeleteRemote(PlannedOperation operation)
    {
        long version = NextVersion(operation);
        var tombstone = new SyncManifestEntry
        {
            Id = operation.ItemId,
            Version = version,
            Deleted = true,
            ModifiedUtc = context.Time.GetUtcNow(),
            ModifiedBy = context.DeviceId,
        };
        return new OperationOutcome(operation, true)
        {
            RemoteEntry = tombstone,
            Baseline = Baseline(operation.ItemId, null, 0, version, deleted: true, null),
        };
    }

    private async Task<OperationOutcome> DownloadAsync(PlannedOperation operation, CancellationToken cancellationToken)
    {
        SyncManifestEntry remote = operation.Remote!;
        LogTransfer(context.Logger, "Download", operation.ItemId, remote.Size);
        var change = new Func<Stream, SyncChange>(content => new SyncChange(SyncChangeKind.Write, operation.ItemId)
        {
            Content = content,
            ExpectedHash = remote.Hash,
            ExpectedLength = remote.Size,
            ExpectedCurrentFingerprint = operation.Local?.Fingerprint,
            ExpectNotExists = operation.Local is null,
        });
        long bytes = await DownloadIntoSourceAsync(remote, change, cancellationToken).ConfigureAwait(false);
        return new OperationOutcome(operation, true, bytes)
        {
            Baseline = Baseline(operation.ItemId, remote.Hash, remote.Size, remote.Version, deleted: false, null),
        };
    }

    private async Task<OperationOutcome> DeleteLocalAsync(PlannedOperation operation, CancellationToken cancellationToken)
    {
        var change = new SyncChange(SyncChangeKind.Delete, operation.ItemId) { ExpectedCurrentFingerprint = operation.Local?.Fingerprint };
        await context.Source.ApplyChangeAsync(change, cancellationToken).ConfigureAwait(false);
        return new OperationOutcome(operation, true)
        {
            Baseline = Baseline(operation.ItemId, null, 0, operation.Remote?.Version ?? 0, deleted: true, null),
        };
    }

    private async Task<OperationOutcome> ConflictCopyAsync(PlannedOperation operation, CancellationToken cancellationToken)
    {
        if (operation.Local is { } local)
        {
            // Download-only: keep the local version as a local copy before the remote version replaces it.
            long copied = await CopyLocalAsync(local, operation.ItemId, cancellationToken).ConfigureAwait(false);
            return new OperationOutcome(operation, true, copied);
        }

        SyncManifestEntry remote = operation.Remote!;
        var entry = remote.Clone();
        entry.Id = operation.ItemId;
        entry.Version = 1;
        entry.Extensions = null;
        long bytes = 0;
        if (operation.WritesLocalCopy)
        {
            bytes = await DownloadIntoSourceAsync(remote, content => new SyncChange(SyncChangeKind.Write, operation.ItemId)
            {
                Content = content,
                ExpectedHash = remote.Hash,
                ExpectedLength = remote.Size,
                ExpectNotExists = true,
            }, cancellationToken).ConfigureAwait(false);
        }

        return new OperationOutcome(operation, true, bytes)
        {
            RemoteEntry = entry,
            Baseline = operation.WritesLocalCopy ? Baseline(operation.ItemId, remote.Hash, remote.Size, 1, deleted: false, null) : null,
        };
    }

    private static OperationOutcome Adopt(PlannedOperation operation)
    {
        if (operation.Remote is null && operation.Local is null) return new OperationOutcome(operation, true) { RemovesState = true };

        SyncManifestEntry? remote = operation.Remote;
        LocalItem? local = operation.Local;
        bool deleted = remote?.Deleted ?? local is null;
        return new OperationOutcome(operation, true)
        {
            Baseline = new ItemSyncState
            {
                ScopeId = string.Empty,
                ItemId = operation.ItemId,
                BaseHash = deleted ? null : remote?.Hash ?? local!.Hash,
                BaseLength = deleted ? 0 : remote?.Size ?? local!.Length,
                BaseVersion = remote?.Version ?? operation.Base?.BaseVersion ?? 0,
                BaseDeleted = deleted,
                LocalFingerprint = local?.Fingerprint,
                LocalHash = local?.Hash,
            },
        };
    }

    /// <summary>Uploads the content object unless an identical one exists; concurrent uploads of the same content are shared.</summary>
    /// <returns>Bytes transferred by this call and the exact content length.</returns>
    private async Task<(long Transferred, long Length)> EnsureObjectAsync(LocalItem local, CancellationToken cancellationToken)
    {
        string path = context.Layout.ObjectPath(RemoteLayout.ObjectName(local.Hash, context.Transform?.TransformId));
        if (context.RemoteObjects.TryGetValue(path, out RemoteSyncItem? existing) && IsSameObject(existing, local)) return (0, existing.Length);

        bool started = false;
        Lazy<Task<long>> upload = _objectUploads.GetOrAdd(path, _ =>
        {
            started = true;
            return new Lazy<Task<long>>(() => UploadObjectAsync(local, path, cancellationToken));
        });
        long length = await upload.Value.ConfigureAwait(false);
        return (started ? length : 0, length);
    }

    /// <summary>
    /// An existing object can be reused when the provider confirms its hash (or, without hashes, its length).
    /// With a content transform the stored length is not the content length, so objects are always uploaded.
    /// </summary>
    private bool IsSameObject(RemoteSyncItem existing, LocalItem local)
        => context.Transform is null
            && (existing.ContentHash is not null ? existing.ContentHash == local.Hash : existing.Length == local.Length);

    private Task<long> UploadObjectAsync(LocalItem local, string path, CancellationToken cancellationToken)
        => context.Retry.ExecuteAsync(async ct =>
        {
            Stream source = await context.ReadLocal(local.Id, ct).ConfigureAwait(false);
            var hashing = new HashingReadStream(source);
            await using (hashing.ConfigureAwait(false))
            {
                Stream payload = context.Transform is null ? hashing : await context.Transform.WrapForUploadAsync(hashing, ct).ConfigureAwait(false);
                RemoteUpload upload = context.Transform is null
                    ? new RemoteUpload(path, ContentHash: local.Hash)
                    : new RemoteUpload(path);
                RemoteSyncItem stored = await context.Provider.UploadAsync(upload, payload, ct).ConfigureAwait(false);
                if (!ReferenceEquals(payload, hashing)) await payload.DisposeAsync().ConfigureAwait(false);

                if (!hashing.ReachedEnd || hashing.Hash != local.Hash)
                {
                    // The content changed after it was hashed: never leave an object whose name lies about its content.
                    await TryDeleteAsync(stored, ct).ConfigureAwait(false);
                    throw new SyncConcurrencyException($"'{local.Id}' changed while it was uploaded; it will be synchronized next time.");
                }

                Interlocked.Add(ref _bytes, hashing.BytesRead);
                return hashing.BytesRead;
            }
        }, cancellationToken);

    private Task<long> DownloadIntoSourceAsync(SyncManifestEntry remote, Func<Stream, SyncChange> createChange, CancellationToken cancellationToken)
    {
        string path = context.Layout.ObjectPath(remote.Object!);
        if (!context.RemoteObjects.TryGetValue(path, out RemoteSyncItem? item))
        {
            throw new SyncIntegrityException($"The content object of '{remote.Id}' is missing remotely; another device may still be uploading it.");
        }

        if (remote.Transform is not null && remote.Transform != context.Transform?.TransformId)
        {
            throw new SyncIntegrityException($"'{remote.Id}' was stored with content transform '{remote.Transform}', which is not configured here.");
        }

        return context.Retry.ExecuteAsync(async ct =>
        {
            Stream downloaded = await context.Provider.DownloadAsync(item, ct).ConfigureAwait(false);
            await using (downloaded.ConfigureAwait(false))
            {
                Stream plain = remote.Transform is null ? downloaded : await context.Transform!.UnwrapAfterDownloadAsync(downloaded, ct).ConfigureAwait(false);
                var verifying = new VerifyingReadStream(plain, remote.Hash!, remote.Size);
                await using (verifying.ConfigureAwait(false))
                {
                    await context.Source.ApplyChangeAsync(createChange(verifying), ct).ConfigureAwait(false);
                    if (!verifying.ReachedEnd) throw new SyncIntegrityException($"The data source did not read the complete content of '{remote.Id}'.");
                    Interlocked.Add(ref _bytes, verifying.BytesRead);
                    return verifying.BytesRead;
                }
            }
        }, cancellationToken);
    }

    private async Task<long> CopyLocalAsync(LocalItem local, string copyId, CancellationToken cancellationToken)
    {
        Stream content = await context.ReadLocal(local.Id, cancellationToken).ConfigureAwait(false);
        var verifying = new VerifyingReadStream(content, local.Hash, local.Length);
        await using (verifying.ConfigureAwait(false))
        {
            await context.Source.ApplyChangeAsync(new SyncChange(SyncChangeKind.Write, copyId)
            {
                Content = verifying,
                ExpectedHash = local.Hash,
                ExpectedLength = local.Length,
                ExpectNotExists = true,
            }, cancellationToken).ConfigureAwait(false);
            return verifying.BytesRead;
        }
    }

    private async Task TryDeleteAsync(RemoteSyncItem item, CancellationToken cancellationToken)
    {
        try
        {
            await context.Provider.DeleteAsync(item, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (SyncException ex)
        {
            LogOperationFailed(context.Logger, context.ScopeId, SyncOperationKind.DeleteRemote, item.Path, ex.Message);
        }
    }

    private static long NextVersion(PlannedOperation operation)
        => Math.Max(operation.Remote?.Version ?? 0, operation.Base?.BaseVersion ?? 0) + 1;

    private static ItemSyncState Baseline(string itemId, string? hash, long length, long version, bool deleted, LocalItem? local) => new()
    {
        ScopeId = string.Empty,
        ItemId = itemId,
        BaseHash = hash,
        BaseLength = length,
        BaseVersion = version,
        BaseDeleted = deleted,
        LocalFingerprint = local?.Fingerprint,
        LocalHash = local?.Hash,
    };

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "{Direction} started: {ItemId} ({Bytes} bytes).")]
    private static partial void LogTransfer(ILogger logger, string direction, string itemId, long bytes);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Warning, Message = "Scope {Scope}: {Kind} of {ItemId} failed: {Reason}")]
    private static partial void LogOperationFailed(ILogger logger, string scope, SyncOperationKind kind, string itemId, string reason);

    [LoggerMessage(EventId = 1022, Level = LogLevel.Information, Message = "Scope {Scope}: {Succeeded} of {Total} operations completed.")]
    private static partial void LogExecuted(ILogger logger, string scope, int succeeded, int total);
}
