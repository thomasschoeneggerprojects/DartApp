using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sta.DataSync.Execution;

namespace Sta.DataSync.Manifest;

/// <summary>The manifest a scope's state is based on (<see cref="Manifest"/> is <see langword="null"/> before the first commit).</summary>
internal sealed record RemoteHead(SyncManifest? Manifest, RemoteSyncItem? Item)
{
    public long Revision => Manifest?.Revision ?? 0;
}

/// <summary>
/// Reads and commits manifests with concurrency control:
/// <list type="bullet">
/// <item>Providers with <see cref="SyncProviderCapabilities.ConditionalWrites"/>: one manifest file replaced with
/// compare-and-swap on its version.</item>
/// <item>Providers with <see cref="SyncProviderCapabilities.ServerCreationOrder"/> (e.g. Google Drive): every commit
/// creates a new revision file. Among files of the same revision the one the server created first wins. A writer waits
/// <see cref="DataSyncOptions.CommitVerificationDelay"/>, lists the revision and only treats its commit as successful
/// when it won; otherwise it deletes its file and re-plans.</item>
/// </list>
/// Providers with neither capability cannot be written to safely; the engine refuses upload modes for them.
/// </summary>
internal sealed partial class ManifestStore(
    ISyncProvider provider,
    RemoteLayout layout,
    DataSyncOptions options,
    RetryPolicy retry,
    TimeProvider time,
    ILogger logger)
{
    public bool UsesConditionalWrites => provider.Capabilities.HasFlag(SyncProviderCapabilities.ConditionalWrites);

    public async Task<RemoteHead> ReadHeadAsync(IReadOnlyList<RemoteSyncItem> listing, CancellationToken cancellationToken)
    {
        RemoteSyncItem? item = UsesConditionalWrites
            ? listing.FirstOrDefault(i => i.Path == layout.ManifestPath)
            : HeadRevisionFile(listing);
        if (item is null) return new RemoteHead(null, null);

        SyncManifest manifest = await DownloadAsync(item, cancellationToken).ConfigureAwait(false);
        if (!UsesConditionalWrites && layout.RevisionOf(item.Path) != manifest.Revision)
        {
            throw new SyncIntegrityException($"The remote manifest {item.Path} claims revision {manifest.Revision}; it is not trusted.");
        }

        return new RemoteHead(manifest, item);
    }

    /// <summary>Commits <paramref name="manifest"/> on top of <paramref name="head"/>.</summary>
    /// <exception cref="SyncConcurrencyException">Another device committed first.</exception>
    public async Task<RemoteSyncItem> CommitAsync(SyncManifest manifest, RemoteHead head, string targetPath, CancellationToken cancellationToken)
    {
        if (UsesConditionalWrites)
        {
            string expected = head.Item?.Version ?? RemoteUpload.CreateOnly;
            return await UploadAsync(new RemoteUpload(targetPath, ExpectedVersion: expected), manifest, cancellationToken).ConfigureAwait(false);
        }

        RemoteSyncItem written = await UploadAsync(new RemoteUpload(targetPath), manifest, cancellationToken).ConfigureAwait(false);
        if (options.CommitVerificationDelay > TimeSpan.Zero)
        {
            await Task.Delay(options.CommitVerificationDelay, time, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<RemoteSyncItem> revisionFiles = await retry
            .ExecuteAsync(ct => provider.ListItemsAsync(layout.RevisionPrefix, ct), cancellationToken)
            .ConfigureAwait(false);
        RemoteSyncItem? winner = WinnerOf(revisionFiles, manifest.Revision);
        if (winner is not null && winner.Path == targetPath) return winner;

        LogLostRace(logger, layout.ScopePrefix, manifest.Revision);
        await TryDeleteAsync(written, cancellationToken).ConfigureAwait(false);
        throw new SyncConcurrencyException($"Another device committed revision {manifest.Revision} of '{layout.ScopePrefix}' first.");
    }

    /// <summary>Path for the next commit.</summary>
    public string NextPath(long revision, string deviceId)
        => UsesConditionalWrites ? layout.ManifestPath : layout.RevisionPath(revision, deviceId, Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());

    /// <summary>
    /// Whether a commit written to <paramref name="path"/> is the accepted revision: <see langword="true"/> when it won,
    /// <see langword="false"/> when another file won or it is missing, <see langword="null"/> when the revision was
    /// already cleaned up (it then stood long enough to be built upon).
    /// </summary>
    public bool? IsAccepted(IReadOnlyList<RemoteSyncItem> listing, string path, string? commitId, RemoteHead head)
    {
        if (UsesConditionalWrites) return head.Manifest is not null && head.Manifest.CommitId == commitId;

        long? revision = layout.RevisionOf(path);
        if (revision is null) return false;
        RemoteSyncItem? winner = WinnerOf(listing, revision.Value);
        if (winner is null) return revision.Value < head.Revision - options.KeepManifestRevisions ? null : false;
        return winner.Path == path;
    }

    /// <summary>Deletes old revisions and unreferenced objects older than the grace period (best effort).</summary>
    public async Task<int> CleanupAsync(IReadOnlyList<RemoteSyncItem> listing, SyncManifest head, CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = time.GetUtcNow() - options.ObjectGracePeriod;
        var referenced = new HashSet<string>(
            head.Items.Where(e => !e.Deleted && e.Object is not null).Select(e => layout.ObjectPath(e.Object!)),
            StringComparer.Ordinal);

        var garbage = new List<RemoteSyncItem>();
        foreach (RemoteSyncItem item in listing)
        {
            bool old = item.CreatedUtc is { } created && created < cutoff;
            if (!old) continue;

            if (item.Path.StartsWith(layout.ObjectPrefix, StringComparison.Ordinal) && !referenced.Contains(item.Path))
            {
                garbage.Add(item);
            }
            else if (layout.RevisionOf(item.Path) is { } revision && IsDisposableRevision(listing, item, revision, head.Revision))
            {
                garbage.Add(item);
            }
        }

        foreach (RemoteSyncItem item in garbage)
        {
            await TryDeleteAsync(item, cancellationToken).ConfigureAwait(false);
        }

        return garbage.Count;
    }

    private bool IsDisposableRevision(IReadOnlyList<RemoteSyncItem> listing, RemoteSyncItem item, long revision, long headRevision)
        => revision < headRevision - options.KeepManifestRevisions || WinnerOf(listing, revision)?.Path != item.Path;

    private RemoteSyncItem? HeadRevisionFile(IReadOnlyList<RemoteSyncItem> listing)
    {
        long? top = listing.Select(i => layout.RevisionOf(i.Path)).Max();
        return top is null ? null : WinnerOf(listing, top.Value);
    }

    /// <summary>Earliest server-created file of a revision (ties broken by provider id), so every device picks the same one.</summary>
    private RemoteSyncItem? WinnerOf(IReadOnlyList<RemoteSyncItem> listing, long revision)
        => listing
            .Where(i => layout.RevisionOf(i.Path) == revision)
            .OrderBy(i => i.CreatedUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.ProviderItemId, StringComparer.Ordinal)
            .FirstOrDefault();

    private async Task<SyncManifest> DownloadAsync(RemoteSyncItem item, CancellationToken cancellationToken)
    {
        if (item.Length > options.MaxManifestBytes)
        {
            throw new SyncIntegrityException($"The remote manifest is larger than {options.MaxManifestBytes} bytes.");
        }

        return await retry.ExecuteAsync(async ct =>
        {
            Stream stream = await provider.DownloadAsync(item, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await ManifestSerializer.ReadAsync(stream, ct).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<RemoteSyncItem> UploadAsync(RemoteUpload upload, SyncManifest manifest, CancellationToken cancellationToken)
        => retry.ExecuteAsync(async ct =>
        {
            using MemoryStream content = ManifestSerializer.Write(manifest);
            return await provider.UploadAsync(upload with { ContentLength = content.Length }, content, ct).ConfigureAwait(false);
        }, cancellationToken);

    private async Task TryDeleteAsync(RemoteSyncItem item, CancellationToken cancellationToken)
    {
        try
        {
            await retry.ExecuteAsync(ct => provider.DeleteAsync(item, cancellationToken: ct), cancellationToken).ConfigureAwait(false);
        }
        catch (SyncException ex)
        {
            LogCleanupFailed(logger, item.Path, ex.Message);
        }
    }

    [LoggerMessage(EventId = 1040, Level = LogLevel.Information, Message = "Another device committed revision {Revision} of {Scope} first; re-planning.")]
    private static partial void LogLostRace(ILogger logger, string scope, long revision);

    [LoggerMessage(EventId = 1041, Level = LogLevel.Debug, Message = "Could not delete remote item {Path}: {Reason}")]
    private static partial void LogCleanupFailed(ILogger logger, string path, string reason);
}
