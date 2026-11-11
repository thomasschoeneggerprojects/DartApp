using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sta.DataSync.Manifest;

namespace Sta.DataSync.Engine;

/// <summary>Recovery, remote change checks, lost-commit handling and cleanup of <see cref="SyncSession"/>.</summary>
internal sealed partial class SyncSession
{
    /// <summary>State recorded for another provider does not describe this destination: start from scratch.</summary>
    private async Task EnsureProviderAsync(CancellationToken cancellationToken)
    {
        ScopeSyncState? scopeState = await _state.GetScopeAsync(ScopeId, cancellationToken).ConfigureAwait(false);
        if (scopeState is not null && scopeState.ProviderId != Provider.ProviderId)
        {
            LogProviderChanged(_logger, ScopeId, scopeState.ProviderId, Provider.ProviderId);
            await _state.ResetScopeAsync(ScopeId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finishes or rolls back a session that was interrupted (crash, forced termination):
    /// <list type="bullet">
    /// <item><c>Executing</c>: local writes are atomic and no metadata was committed, so the next comparison sees equal
    /// content on both sides and adopts it. The entry is dropped.</item>
    /// <item><c>Committing</c>: if the written manifest became the accepted revision, its state is committed now;
    /// otherwise it is discarded and the changes are planned again.</item>
    /// </list>
    /// </summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        SyncJournalEntry? entry = await _state.GetOpenJournalAsync(ScopeId, cancellationToken).ConfigureAwait(false);
        if (entry is null) return;

        LogRecovery(_logger, ScopeId, entry.SessionId, entry.Phase);
        CommitJournal? commit = entry.Phase == JournalPhase.Committing && entry.Payload is not null
            ? JsonSerializer.Deserialize<CommitJournal>(entry.Payload, JsonOptions)
            : null;
        if (commit is null)
        {
            await _state.DeleteJournalAsync(entry.SessionId, cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<RemoteSyncItem> listing = await _retry.ExecuteAsync(ct => Provider.ListItemsAsync(_layout.ListPrefix, ct), cancellationToken).ConfigureAwait(false);
        RemoteHead head = await _manifests.ReadHeadAsync(listing, cancellationToken).ConfigureAwait(false);
        if (_manifests.IsAccepted(listing, commit.ManifestPath, commit.CommitId, head) != false)
        {
            await _state.CommitAsync(commit.StateCommit with { CompleteJournalSession = entry.SessionId }, cancellationToken).ConfigureAwait(false);
            return;
        }

        RemoteSyncItem? orphan = listing.FirstOrDefault(i => i.Path == commit.ManifestPath && !_manifests.UsesConditionalWrites);
        if (orphan is not null) await Provider.DeleteAsync(orphan, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _state.DeleteJournalAsync(entry.SessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Uses the provider's change feed to skip remote listing when nothing changed. Returns whether the remote side is
    /// known to be unchanged and the token to store after a successful run (taken before any listing, so concurrent
    /// remote changes are never missed).
    /// </summary>
    private async Task<(bool Unchanged, string? Token)> CheckRemoteChangesAsync(ScopeSyncState? scopeState, CancellationToken cancellationToken)
    {
        if (Provider is not IRemoteChangeFeed feed || !Provider.Capabilities.HasFlag(SyncProviderCapabilities.ChangeFeed)) return (false, null);

        if (scopeState?.ChangeToken is { } token)
        {
            try
            {
                RemoteChangeSet changes = await _retry.ExecuteAsync(ct => feed.GetChangesAsync(token, _layout.ListPrefix, ct), cancellationToken).ConfigureAwait(false);
                return (!changes.HasChanges, changes.NextToken);
            }
            catch (SyncConcurrencyException)
            {
                // Expired token: fall back to a full listing with a fresh token.
            }
        }

        string start = await _retry.ExecuteAsync(ct => feed.GetStartTokenAsync(ct), cancellationToken).ConfigureAwait(false);
        return (false, start);
    }

    /// <summary>
    /// Detects that this device's last commit lost a race after the fact (possible only with eventually consistent
    /// listings) and restores the baselines from before that commit, so its changes are planned again (as uploads or
    /// conflicts) instead of being overwritten by the winning revision.
    /// </summary>
    private async Task<bool> RestoreLostCommitAsync(ScopeSyncState? scopeState, IReadOnlyList<RemoteSyncItem> listing, RemoteHead head, CancellationToken cancellationToken)
    {
        if (scopeState?.OwnCommitPath is not { } own || _manifests.UsesConditionalWrites) return false;

        bool? accepted = _manifests.IsAccepted(listing, own, null, head);
        if (accepted == true) return false;

        ScopeSyncState cleared = scopeState with { OwnCommitPath = null, OwnCommitRollback = null };
        if (accepted is null)
        {
            await _state.CommitAsync(new SyncStateCommit { ScopeId = ScopeId, Scope = cleared }, cancellationToken).ConfigureAwait(false);
            return false;
        }

        LogLostCommit(_logger, ScopeId, own);
        List<RollbackEntry> entries = scopeState.OwnCommitRollback is null
            ? []
            : JsonSerializer.Deserialize<List<RollbackEntry>>(scopeState.OwnCommitRollback, JsonOptions) ?? [];
        await _state.CommitAsync(new SyncStateCommit
        {
            ScopeId = ScopeId,
            Scope = cleared,
            UpsertItems = entries.Where(e => e.Previous is not null).Select(e => e.Previous! with { LocalFingerprint = null, LocalHash = null }).ToList(),
            RemoveItems = entries.Where(e => e.Previous is null).Select(e => e.ItemId).ToList(),
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Deletes old manifest revisions and unreferenced objects at most once per <see cref="DataSyncOptions.CleanupInterval"/>.</summary>
    private async Task CleanupIfDueAsync(AttemptContext attempt, RemoteHead head, CancellationToken cancellationToken)
    {
        if (!_settings.Uploads || head.Manifest is null) return;
        DateTimeOffset now = _time.GetUtcNow();
        if (attempt.ScopeState?.LastCleanupUtc is { } last && now - last < _options.CleanupInterval) return;

        try
        {
            int deleted = await _manifests.CleanupAsync(attempt.Listing, head.Manifest, cancellationToken).ConfigureAwait(false);
            ScopeSyncState? current = await _state.GetScopeAsync(ScopeId, cancellationToken).ConfigureAwait(false);
            if (current is not null) await _state.CommitAsync(new SyncStateCommit { ScopeId = ScopeId, Scope = current with { LastCleanupUtc = now } }, cancellationToken).ConfigureAwait(false);
            LogCleanup(_logger, ScopeId, deleted);
        }
        catch (SyncException ex)
        {
            LogFailed(_logger, ScopeId, "Remote cleanup failed: " + ex.Message, ex);
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Synchronization of {Scope} started (session {SessionId}, {Mode}).")]
    private static partial void LogStarted(ILogger logger, string scope, string sessionId, SyncMode mode);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Synchronization of {Scope} finished: {Status}, {Uploaded} up, {Downloaded} down, {Deleted} deleted, {Conflicts} conflicts, {Bytes} bytes in {ElapsedMs:0} ms.")]
    private static partial void LogCompleted(ILogger logger, string scope, SyncResultStatus status, int uploaded, int downloaded, int deleted, int conflicts, long bytes, double elapsedMs);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Synchronization of {Scope} was canceled.")]
    private static partial void LogCanceled(ILogger logger, string scope);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error, Message = "Synchronization of {Scope} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string scope, string reason, Exception exception);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Scope {Scope}: {Count} local changes detected.")]
    private static partial void LogLocalChanges(ILogger logger, string scope, int count);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information, Message = "Scope {Scope}: remote changes detected (revision {From} -> {To}).")]
    private static partial void LogRemoteChanges(ILogger logger, string scope, long from, long to);

    [LoggerMessage(EventId = 1050, Level = LogLevel.Warning, Message = "Scope {Scope}: conflict detected for {ItemId}; both versions are kept until it is resolved.")]
    private static partial void LogConflict(ILogger logger, string scope, string itemId);

    [LoggerMessage(EventId = 1060, Level = LogLevel.Warning, Message = "Scope {Scope}: recovering interrupted session {SessionId} (phase {Phase}).")]
    private static partial void LogRecovery(ILogger logger, string scope, string sessionId, string phase);

    [LoggerMessage(EventId = 1061, Level = LogLevel.Warning, Message = "Scope {Scope}: this device's commit {Path} lost a concurrent race; its changes are planned again.")]
    private static partial void LogLostCommit(ILogger logger, string scope, string path);

    [LoggerMessage(EventId = 1062, Level = LogLevel.Warning, Message = "Scope {Scope}: provider changed from {Previous} to {Current}; synchronization state is reset.")]
    private static partial void LogProviderChanged(ILogger logger, string scope, string previous, string current);

    [LoggerMessage(EventId = 1063, Level = LogLevel.Debug, Message = "Scope {Scope}: remote cleanup deleted {Count} items.")]
    private static partial void LogCleanup(ILogger logger, string scope, int count);

    [LoggerMessage(EventId = 1070, Level = LogLevel.Warning, Message = "Scope {Scope}: authentication failed ({Reason}); automatic synchronization waits for sign-in.")]
    private static partial void LogAuthenticationFailed(ILogger logger, string scope, string reason);
}
