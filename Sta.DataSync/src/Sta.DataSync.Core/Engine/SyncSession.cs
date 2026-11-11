using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sta.DataSync.Conflicts;
using Sta.DataSync.Detection;
using Sta.DataSync.Execution;
using Sta.DataSync.Manifest;
using Sta.DataSync.Planning;
using ExecutionContext = Sta.DataSync.Execution.ExecutionContext;

namespace Sta.DataSync.Engine;

/// <summary>
/// One synchronization run of one scope:
/// recover → detect local changes → check remote changes → read head manifest → plan (three-way) → resolve conflicts →
/// execute → commit manifest (with concurrency control) → commit state in one transaction → clean up.
/// Metadata is committed only for operations that succeeded; a lost commit race re-plans from fresh remote state.
/// </summary>
internal sealed partial class SyncSession
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SyncScopeDefinition _scope;
    private readonly ScopeSettings _settings;
    private readonly DataSyncOptions _options;
    private readonly ISyncStateStore _state;
    private readonly IConflictResolver? _customResolver;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Action<SyncProgress> _reportProgress;
    private readonly RemoteLayout _layout;
    private readonly RetryPolicy _retry;
    private readonly ManifestStore _manifests;
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..12];
    private string _deviceId = string.Empty;
    private DateTimeOffset _started;

    public SyncSession(
        SyncScopeDefinition scope,
        ScopeSettings settings,
        DataSyncOptions options,
        ISyncStateStore state,
        IConflictResolver? customResolver,
        TimeProvider time,
        ILogger logger,
        Action<SyncProgress> reportProgress)
    {
        _scope = scope;
        _settings = settings;
        _options = options;
        _state = state;
        _customResolver = customResolver;
        _time = time;
        _logger = logger;
        _reportProgress = reportProgress;
        _layout = new RemoteLayout(options.RootFolder, options.ApplicationId, scope.ScopeId);
        _retry = new RetryPolicy(options.Retry, time, logger);
        _manifests = new ManifestStore(scope.Provider, _layout, options, _retry, time, logger);
    }

    private string ScopeId => _scope.ScopeId;

    private ISyncProvider Provider => _scope.Provider;

    public async Task<SyncResult> RunAsync(CancellationToken cancellationToken)
    {
        _started = _time.GetUtcNow();
        using IDisposable? logScope = _logger.BeginScope(new Dictionary<string, object> { ["SyncSessionId"] = _sessionId, ["SyncScope"] = ScopeId });
        LogStarted(_logger, ScopeId, _sessionId, _settings.Mode);
        try
        {
            _deviceId = await _state.GetDeviceIdAsync(cancellationToken).ConfigureAwait(false);
            await EnsureProviderAsync(cancellationToken).ConfigureAwait(false);
            await RecoverAsync(cancellationToken).ConfigureAwait(false);

            for (int attempt = 1; ; attempt++)
            {
                SyncResult? result = await RunAttemptAsync(attempt == _options.MaxCommitAttempts, cancellationToken).ConfigureAwait(false);
                if (result is not null) return Completed(result);
            }
        }
        catch (SyncAuthenticationException ex)
        {
            LogAuthenticationFailed(_logger, ScopeId, ex.Message);
            return await FailedAsync(SyncResultStatus.AuthenticationRequired, ex.Message, transient: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogCanceled(_logger, ScopeId);
            return await FailedAsync(SyncResultStatus.Canceled, "Synchronization was canceled.", transient: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SyncException or IOException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException)
        {
            LogFailed(_logger, ScopeId, ex.Message, ex);
            return await FailedAsync(SyncResultStatus.Failed, ex.Message, RetryPolicy.IsTransient(ex, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>One plan-execute-commit pass; <see langword="null"/> means "lost a concurrent commit, plan again".</summary>
    private async Task<SyncResult?> RunAttemptAsync(bool lastAttempt, CancellationToken cancellationToken)
    {
        ScopeSyncState? scopeState = await _state.GetScopeAsync(ScopeId, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, ItemSyncState> items = await _state.GetItemsAsync(ScopeId, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, ConflictResolution> decisions = await _state.GetConflictResolutionsAsync(ScopeId, cancellationToken).ConfigureAwait(false);

        LocalView view = await LocalView.OpenAsync(_scope.Source, cancellationToken).ConfigureAwait(false);
        await using (view.ConfigureAwait(false))
        {
            LocalDetection local = await LocalChangeDetector.DetectAsync(view.Items, view.Reader, items, _logger, cancellationToken).ConfigureAwait(false);
            int localChanges = CountLocalChanges(local, items);
            if (localChanges > 0) LogLocalChanges(_logger, ScopeId, localChanges);

            (bool remoteUnchanged, string? changeToken) = await CheckRemoteChangesAsync(scopeState, cancellationToken).ConfigureAwait(false);
            if (remoteUnchanged && localChanges == 0 && decisions.Count == 0 && scopeState?.ManifestPath is not null)
            {
                await CommitStateAsync(BuildIdleCommit(scopeState, local, items, changeToken), cancellationToken).ConfigureAwait(false);
                return Result(SyncResultStatus.NoChanges, [], SyncPlan.Empty, [], []);
            }

            IReadOnlyList<RemoteSyncItem> listing = await _retry
                .ExecuteAsync(ct => Provider.ListItemsAsync(_layout.ListPrefix, ct), cancellationToken)
                .ConfigureAwait(false);
            RemoteHead head = await _manifests.ReadHeadAsync(listing, cancellationToken).ConfigureAwait(false);
            if (head.Revision != (scopeState?.ManifestRevision ?? 0)) LogRemoteChanges(_logger, ScopeId, scopeState?.ManifestRevision ?? 0, head.Revision);

            if (await RestoreLostCommitAsync(scopeState, listing, head, cancellationToken).ConfigureAwait(false))
            {
                scopeState = await _state.GetScopeAsync(ScopeId, cancellationToken).ConfigureAwait(false);
                items = await _state.GetItemsAsync(ScopeId, cancellationToken).ConfigureAwait(false);
            }

            return await PlanAndExecuteAsync(new AttemptContext(scopeState, items, decisions, local, view, listing, head, changeToken, lastAttempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SyncResult?> PlanAndExecuteAsync(AttemptContext attempt, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, SyncManifestEntry> remote = attempt.Head.Manifest?.ToDictionary() ?? new Dictionary<string, SyncManifestEntry>(StringComparer.Ordinal);
        SyncPlan raw = SyncPlanner.Plan(new PlanningInput(attempt.Local.Items, remote, attempt.Items, _settings.Mode, _settings.DeletionPolicy));

        IReadOnlyList<SyncConflict> storedConflicts = await _state.GetConflictsAsync(ScopeId, cancellationToken).ConfigureAwait(false);
        var resolver = new ConflictResolutionService(ScopeId, _settings.Mode, _settings.Strategy, _customResolver, _deviceId, _time, _scope.Source.Capabilities);
        var existingIds = new HashSet<string>(attempt.Local.Items.Keys.Concat(remote.Keys), StringComparer.Ordinal);
        ResolvedPlan resolved = await resolver.ResolveAsync(
            raw,
            storedConflicts.ToDictionary(c => c.ItemId, StringComparer.Ordinal),
            attempt.Decisions,
            existingIds,
            cancellationToken).ConfigureAwait(false);
        foreach (SyncConflict conflict in resolved.Unresolved) LogConflict(_logger, ScopeId, conflict.ItemId);

        SyncPlan plan = resolved.Plan;
        if (plan.HasTransfers)
        {
            await _state.WriteJournalAsync(new SyncJournalEntry(_sessionId, ScopeId, JournalPhase.Executing, null, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }

        ExecutionReport report = await ExecuteAsync(plan, attempt, cancellationToken).ConfigureAwait(false);
        return await CommitAsync(attempt, plan, resolved, report, cancellationToken).ConfigureAwait(false);
    }

    private Task<ExecutionReport> ExecuteAsync(SyncPlan plan, AttemptContext attempt, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, RemoteSyncItem> objects = attempt.Listing
            .Where(i => i.Path.StartsWith(_layout.ObjectPrefix, StringComparison.Ordinal))
            .GroupBy(i => i.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.ModifiedUtc).First(), StringComparer.Ordinal);
        int total = plan.Operations.Count;
        var context = new ExecutionContext(
            ScopeId,
            _scope.Source,
            Provider,
            attempt.View.Reader,
            _layout,
            objects,
            _scope.ContentTransform,
            _retry,
            _deviceId,
            _options.MaxParallelTransfers,
            _time,
            _logger,
            (completed, bytes, item) => _reportProgress(new SyncProgress(completed, total, bytes, item) { ScopeId = ScopeId }));
        _reportProgress(new SyncProgress(0, total, 0, null) { ScopeId = ScopeId });
        return new SyncExecutor(context).ExecuteAsync(plan, cancellationToken);
    }

    private async Task<SyncResult?> CommitAsync(AttemptContext attempt, SyncPlan plan, ResolvedPlan resolved, ExecutionReport report, CancellationToken cancellationToken)
    {
        // Finishing a commit is short and keeps remote and local metadata consistent, so it is not canceled midway.
        CancellationToken commitToken = CancellationToken.None;
        List<OperationOutcome> succeeded = report.Outcomes.Where(o => o.Succeeded).ToList();
        List<OperationOutcome> remoteChanges = succeeded.Where(o => o.RemoteEntry is not null).ToList();
        var failures = report.Outcomes.Where(o => !o.Succeeded).Select(o => new SyncFailure(ScopeId, o.Operation.ItemId, o.Error ?? "Failed.", o.Transient)).ToList();

        RemoteHead newHead = attempt.Head;
        string? ownCommitPath = attempt.ScopeState?.OwnCommitPath;
        string? rollback = attempt.ScopeState?.OwnCommitRollback;

        if (remoteChanges.Count > 0)
        {
            SyncManifest manifest = BuildManifest(attempt.Head, remoteChanges);
            string path = _manifests.NextPath(manifest.Revision, _deviceId);
            string newRollback = JsonSerializer.Serialize(remoteChanges.Select(o => new RollbackEntry(o.Operation.ItemId, attempt.Items.GetValueOrDefault(o.Operation.ItemId))).ToList(), JsonOptions);
            string? newOwnPath = _manifests.UsesConditionalWrites ? null : path;
            SyncStateCommit pending = BuildCommit(attempt, resolved, report, succeeded, revision: manifest.Revision, manifestPath: path, newOwnPath, newRollback, failures);
            await _state.WriteJournalAsync(
                new SyncJournalEntry(_sessionId, ScopeId, JournalPhase.Committing, JsonSerializer.Serialize(new CommitJournal(path, _sessionId, pending), JsonOptions), _time.GetUtcNow()),
                commitToken).ConfigureAwait(false);

            try
            {
                RemoteSyncItem written = await _manifests.CommitAsync(manifest, attempt.Head, path, commitToken).ConfigureAwait(false);
                newHead = new RemoteHead(manifest, written);
                ownCommitPath = newOwnPath;
                rollback = newOwnPath is null ? null : newRollback;
                await CommitStateAsync(pending, commitToken).ConfigureAwait(false);
            }
            catch (SyncConcurrencyException ex)
            {
                // Lost the race: keep only local-side results (they match the head we read) and plan again.
                List<OperationOutcome> localOnly = succeeded.Where(o => o.RemoteEntry is null && !o.Operation.ChangesRemote).ToList();
                await CommitStateAsync(BuildCommit(attempt, resolved, report, localOnly, attempt.Head.Revision, attempt.Head.Item?.Path, attempt.ScopeState?.OwnCommitPath, attempt.ScopeState?.OwnCommitRollback, failures), commitToken).ConfigureAwait(false);
                if (!attempt.LastAttempt) return null;

                failures.Add(new SyncFailure(ScopeId, null, ex.Message, IsTransient: true));
                return Result(SyncResultStatus.PartiallySucceeded, localOnly, plan, resolved.Unresolved, failures);
            }
        }
        else
        {
            await CommitStateAsync(BuildCommit(attempt, resolved, report, succeeded, newHead.Revision, newHead.Item?.Path, ownCommitPath, rollback, failures), commitToken).ConfigureAwait(false);
        }

        if (report.AuthenticationFailure is not null) throw report.AuthenticationFailure;
        await CleanupIfDueAsync(attempt, newHead, commitToken).ConfigureAwait(false);

        SyncResultStatus status = report.Canceled ? SyncResultStatus.Canceled
            : failures.Count > 0 || resolved.Unresolved.Count > 0 ? (succeeded.Count > 0 || failures.Count == 0 ? SyncResultStatus.PartiallySucceeded : SyncResultStatus.Failed)
            : plan.HasTransfers ? SyncResultStatus.Succeeded
            : SyncResultStatus.NoChanges;
        return Result(status, succeeded, plan, resolved.Unresolved, failures);
    }

    private SyncManifest BuildManifest(RemoteHead head, IReadOnlyList<OperationOutcome> remoteChanges)
    {
        var entries = new Dictionary<string, SyncManifestEntry>(StringComparer.Ordinal);
        foreach (SyncManifestEntry entry in head.Manifest?.Items ?? []) entries[entry.Id] = entry;
        foreach (OperationOutcome outcome in remoteChanges) entries[outcome.RemoteEntry!.Id] = outcome.RemoteEntry;

        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset tombstoneCutoff = now - _options.TombstoneRetention;
        return new SyncManifest
        {
            Revision = head.Revision + 1,
            Parent = head.Item?.Path,
            CommitId = _sessionId,
            DeviceId = _deviceId,
            CreatedUtc = now,
            Extensions = head.Manifest?.Extensions,
            Items = entries.Values
                .Where(e => !(e.Deleted && e.ModifiedUtc is { } modified && modified < tombstoneCutoff))
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private SyncStateCommit BuildCommit(
        AttemptContext attempt,
        ResolvedPlan resolved,
        ExecutionReport report,
        IReadOnlyList<OperationOutcome> applied,
        long revision,
        string? manifestPath,
        string? ownCommitPath,
        string? rollback,
        IReadOnlyList<SyncFailure> failures)
    {
        var upserts = new Dictionary<string, ItemSyncState>(StringComparer.Ordinal);
        var removals = new List<string>();
        foreach (OperationOutcome outcome in applied)
        {
            if (outcome.RemovesState) removals.Add(outcome.Operation.ItemId);
            else if (outcome.Baseline is { } baseline) upserts[baseline.ItemId] = baseline with { ScopeId = ScopeId };
        }

        AddHashCache(attempt, upserts);
        HashSet<string> failedGroups = report.Outcomes.Where(o => !o.Succeeded).Select(o => o.Operation.GroupId).ToHashSet(StringComparer.Ordinal);
        DateTimeOffset now = _time.GetUtcNow();
        bool success = failures.Count == 0 && !report.Canceled && report.AuthenticationFailure is null;

        return new SyncStateCommit
        {
            ScopeId = ScopeId,
            Scope = new ScopeSyncState
            {
                ScopeId = ScopeId,
                ProviderId = Provider.ProviderId,
                ManifestRevision = revision,
                ManifestPath = manifestPath,
                OwnCommitPath = ownCommitPath,
                OwnCommitRollback = rollback,
                ChangeToken = attempt.ChangeToken ?? attempt.ScopeState?.ChangeToken,
                LastSuccessUtc = success ? now : attempt.ScopeState?.LastSuccessUtc,
                LastAttemptUtc = now,
                LastError = success ? null : (failures.Count > 0 ? failures[0].Message : report.AuthenticationFailure?.Message),
                LastCleanupUtc = attempt.ScopeState?.LastCleanupUtc,
            },
            UpsertItems = upserts.Values.ToList(),
            RemoveItems = removals,
            UpsertConflicts = resolved.Unresolved,
            RemoveConflicts = resolved.ResolvedItemIds.Where(id => !failedGroups.Contains(id)).ToList(),
            CompleteJournalSession = _sessionId,
        };
    }

    /// <summary>Remembers hashes of unchanged local items so the next run does not read them again.</summary>
    private void AddHashCache(AttemptContext attempt, Dictionary<string, ItemSyncState> upserts)
    {
        foreach (LocalItem local in attempt.Local.Items.Values)
        {
            if (local.Fingerprint is null) continue;
            if (upserts.TryGetValue(local.Id, out ItemSyncState? pending))
            {
                if (pending.LocalFingerprint is null && pending.BaseHash == local.Hash) upserts[local.Id] = pending with { LocalFingerprint = local.Fingerprint, LocalHash = local.Hash };
                continue;
            }

            ItemSyncState existing = attempt.Items.GetValueOrDefault(local.Id) ?? new ItemSyncState { ScopeId = ScopeId, ItemId = local.Id };
            if (existing.LocalFingerprint != local.Fingerprint || existing.LocalHash != local.Hash)
            {
                upserts[local.Id] = existing with { ScopeId = ScopeId, LocalFingerprint = local.Fingerprint, LocalHash = local.Hash };
            }
        }
    }

    private SyncStateCommit BuildIdleCommit(ScopeSyncState scopeState, LocalDetection local, IReadOnlyDictionary<string, ItemSyncState> items, string? changeToken)
    {
        var upserts = new Dictionary<string, ItemSyncState>(StringComparer.Ordinal);
        var attempt = new AttemptContext(scopeState, items, new Dictionary<string, ConflictResolution>(), local, null!, [], new RemoteHead(null, null), changeToken, false);
        AddHashCache(attempt, upserts);
        DateTimeOffset now = _time.GetUtcNow();
        return new SyncStateCommit
        {
            ScopeId = ScopeId,
            Scope = scopeState with { ChangeToken = changeToken ?? scopeState.ChangeToken, LastSuccessUtc = now, LastAttemptUtc = now, LastError = null },
            UpsertItems = upserts.Values.ToList(),
        };
    }

    private Task CommitStateAsync(SyncStateCommit commit, CancellationToken cancellationToken) => _state.CommitAsync(commit, cancellationToken);

    private static int CountLocalChanges(LocalDetection local, IReadOnlyDictionary<string, ItemSyncState> items)
    {
        int changes = local.Items.Values.Count(l => !items.TryGetValue(l.Id, out ItemSyncState? s) || !s.HasBaseline || s.BaseDeleted || s.BaseHash != l.Hash);
        changes += items.Values.Count(s => s.HasBaseline && !s.BaseDeleted && !local.Items.ContainsKey(s.ItemId));
        return changes;
    }

    private SyncResult Completed(SyncResult result)
    {
        LogCompleted(_logger, ScopeId, result.Status, result.UploadedCount, result.DownloadedCount, result.DeletedCount, result.ConflictCount, result.TransferredBytes, result.Duration.TotalMilliseconds);
        return result;
    }

    private SyncResult Result(SyncResultStatus status, IReadOnlyList<OperationOutcome> succeeded, SyncPlan plan, IReadOnlyList<SyncConflict> conflicts, IReadOnlyList<SyncFailure> failures)
    {
        int Count(params SyncOperationKind[] kinds) => succeeded.Count(o => kinds.Contains(o.Operation.Kind));
        return new SyncResult
        {
            Status = status,
            ScopeId = ScopeId,
            SessionId = _sessionId,
            StartedUtc = _started,
            CompletedUtc = _time.GetUtcNow(),
            UploadedCount = Count(SyncOperationKind.Upload) + succeeded.Count(o => o.Operation.Kind == SyncOperationKind.UploadConflictCopy && o.RemoteEntry is not null),
            DownloadedCount = Count(SyncOperationKind.Download) + succeeded.Count(o => o.Operation.Kind == SyncOperationKind.UploadConflictCopy && o.Operation.WritesLocalCopy),
            DeletedCount = Count(SyncOperationKind.DeleteLocal, SyncOperationKind.DeleteRemote),
            SkippedCount = plan.Skipped.Count,
            TransferredBytes = succeeded.Sum(o => o.Bytes),
            Conflicts = conflicts,
            Failures = failures,
            Operations = succeeded
                .Select(o => new SyncOperationResult(o.Operation.ItemId, o.Operation.Kind, true, o.Bytes))
                .Concat(failures.Where(f => f.ItemId is not null).Select(f => new SyncOperationResult(f.ItemId!, OperationKindOf(plan, f.ItemId!), false, Error: f.Message)))
                .ToList(),
        };
    }

    private static SyncOperationKind OperationKindOf(SyncPlan plan, string itemId)
        => plan.Operations.FirstOrDefault(o => o.ItemId == itemId)?.Kind ?? SyncOperationKind.Conflict;

    private async Task<SyncResult> FailedAsync(SyncResultStatus status, string message, bool transient)
    {
        try
        {
            ScopeSyncState? current = await _state.GetScopeAsync(ScopeId, CancellationToken.None).ConfigureAwait(false);
            ScopeSyncState updated = (current ?? new ScopeSyncState { ScopeId = ScopeId, ProviderId = Provider.ProviderId })
                with { LastAttemptUtc = _time.GetUtcNow(), LastError = message };
            await _state.CommitAsync(new SyncStateCommit { ScopeId = ScopeId, Scope = updated }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SyncException or IOException or InvalidOperationException)
        {
            LogFailed(_logger, ScopeId, "Could not record the failed attempt: " + ex.Message, ex);
        }

        return Completed(new SyncResult
        {
            Status = status,
            ScopeId = ScopeId,
            SessionId = _sessionId,
            StartedUtc = _started,
            CompletedUtc = _time.GetUtcNow(),
            Failures = [new SyncFailure(ScopeId, null, message, transient)],
        });
    }

    /// <summary>Per-attempt data.</summary>
    private sealed record AttemptContext(
        ScopeSyncState? ScopeState,
        IReadOnlyDictionary<string, ItemSyncState> Items,
        IReadOnlyDictionary<string, ConflictResolution> Decisions,
        LocalDetection Local,
        LocalView View,
        IReadOnlyList<RemoteSyncItem> Listing,
        RemoteHead Head,
        string? ChangeToken,
        bool LastAttempt);
}
