using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sta.DataSync.Engine;
using Sta.DataSync.Validation;

namespace Sta.DataSync;

/// <summary>
/// Shared activity flags written by schedulers or platform integrations (paused, offline) and read by
/// <see cref="DataSyncService"/> for <see cref="SyncStatus.Activity"/>. Thread-safe.
/// </summary>
public sealed class SyncActivityState
{
    private readonly object _gate = new();
    private bool _paused;
    private bool _offline;

    /// <summary>Raised when a flag changes.</summary>
    public event EventHandler? Changed;

    /// <summary><see langword="true"/> while automatic synchronization is paused.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate) return _paused;
        }

        set => Set(ref _paused, value);
    }

    /// <summary><see langword="true"/> while the device is offline.</summary>
    public bool IsOffline
    {
        get
        {
            lock (_gate) return _offline;
        }

        set => Set(ref _offline, value);
    }

    private void Set(ref bool field, bool value)
    {
        lock (_gate)
        {
            if (field == value) return;
            field = value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Default <see cref="IDataSyncService"/>: runs <see cref="SyncScopeDefinition"/>s with the provider-independent engine.</summary>
public sealed class DataSyncService : IDataSyncService, IDisposable
{
    private readonly IReadOnlyDictionary<string, SyncScopeDefinition> _scopes;
    private readonly ISyncStateStore _state;
    private readonly DataSyncOptions _options;
    private readonly IConflictResolver? _customResolver;
    private readonly SyncActivityState _activity;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SyncCoordinator _coordinator = new();
    private readonly object _progressGate = new();
    private SyncProgress? _progress;
    private int _running;

    /// <summary>Creates the service and validates every scope against its provider's and source's capabilities.</summary>
    /// <exception cref="SyncConfigurationException">Options or a scope are invalid.</exception>
    public DataSyncService(
        IEnumerable<SyncScopeDefinition> scopes,
        ISyncStateStore stateStore,
        IOptions<DataSyncOptions> options,
        IEnumerable<IConflictResolver>? conflictResolvers = null,
        SyncActivityState? activity = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        _state = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _options = options.Value;
        IReadOnlyList<string> errors = _options.Validate();
        if (errors.Count > 0) throw new SyncConfigurationException("Invalid DataSyncOptions: " + string.Join(" ", errors));

        _customResolver = conflictResolvers?.LastOrDefault();
        _activity = activity ?? new SyncActivityState();
        _time = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _scopes = BuildScopes(scopes);
        _activity.Changed += (_, _) => RaiseStatusChanged();
    }

    /// <inheritdoc />
    public event EventHandler<SyncStatusChangedEventArgs>? StatusChanged;

    /// <inheritdoc />
    public event EventHandler<SyncProgressEventArgs>? ProgressChanged;

    /// <inheritdoc />
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    /// <inheritdoc />
    public IReadOnlyList<string> ScopeIds => _scopes.Keys.Order(StringComparer.Ordinal).ToList();

    /// <inheritdoc />
    public Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
        => SynchronizeAsync(null, null, cancellationToken);

    /// <inheritdoc />
    public Task<SyncResult> SynchronizeAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return SynchronizeAsync(scopeId, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SyncResult> SynchronizeAsync(string? scopeId, IProgress<SyncProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (scopeId is not null)
        {
            if (!_scopes.TryGetValue(scopeId, out SyncScopeDefinition? scope)) throw new SyncConfigurationException($"Unknown synchronization scope '{scopeId}'.");
            return await RunScopeAsync(scope, progress, cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset started = _time.GetUtcNow();
        SyncResult[] results = await Task.WhenAll(_scopes.Values.Select(s => RunScopeAsync(s, progress, cancellationToken))).ConfigureAwait(false);
        return Aggregate(results, started);
    }

    /// <inheritdoc />
    public async Task<SyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SyncConflict> conflicts = await _state.GetConflictsAsync(null, cancellationToken).ConfigureAwait(false);
        var scopes = new List<SyncScopeStatus>(_scopes.Count);
        foreach (SyncScopeDefinition scope in _scopes.Values.OrderBy(s => s.ScopeId, StringComparer.Ordinal))
        {
            ScopeSyncState? state = await _state.GetScopeAsync(scope.ScopeId, cancellationToken).ConfigureAwait(false);
            scopes.Add(new SyncScopeStatus(
                scope.ScopeId,
                scope.Provider.ProviderId,
                scope.Mode ?? _options.DefaultMode,
                state?.LastSuccessUtc,
                state?.LastAttemptUtc,
                state?.LastError,
                conflicts.Count(c => c.ScopeId == scope.ScopeId)));
        }

        return new SyncStatus(CurrentActivity, scopes, CurrentProgress);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(string? scopeId = null, CancellationToken cancellationToken = default)
        => _state.GetConflictsAsync(scopeId, cancellationToken);

    /// <inheritdoc />
    public Task ResolveConflictAsync(string scopeId, string itemId, ConflictResolution resolution, CancellationToken cancellationToken = default)
    {
        if (!_scopes.ContainsKey(scopeId)) throw new SyncConfigurationException($"Unknown synchronization scope '{scopeId}'.");
        return _state.SetConflictResolutionAsync(scopeId, itemId, resolution, cancellationToken);
    }

    /// <summary>Releases the per-scope locks.</summary>
    public void Dispose() => _coordinator.Dispose();

    private SyncActivity CurrentActivity
        => Volatile.Read(ref _running) > 0 ? SyncActivity.Synchronizing
            : _activity.IsOffline ? SyncActivity.Offline
            : _activity.IsPaused ? SyncActivity.Paused
            : SyncActivity.Idle;

    private SyncProgress? CurrentProgress
    {
        get
        {
            lock (_progressGate) return _progress;
        }
    }

    private Task<SyncResult> RunScopeAsync(SyncScopeDefinition scope, IProgress<SyncProgress>? progress, CancellationToken cancellationToken)
        => _coordinator.RunAsync(scope.ScopeId, async ct =>
        {
            Interlocked.Increment(ref _running);
            RaiseStatusChanged();
            try
            {
                var session = new SyncSession(
                    scope,
                    SettingsOf(scope),
                    _options,
                    _state,
                    _customResolver,
                    _time,
                    _loggerFactory.CreateLogger("Sta.DataSync.Engine"),
                    p => ReportProgress(p, progress));
                SyncResult result = await session.RunAsync(ct).ConfigureAwait(false);
                SyncCompleted?.Invoke(this, new SyncCompletedEventArgs(result));
                return result;
            }
            finally
            {
                if (Interlocked.Decrement(ref _running) == 0)
                {
                    lock (_progressGate) _progress = null;
                }

                RaiseStatusChanged();
            }
        }, cancellationToken);

    private void ReportProgress(SyncProgress value, IProgress<SyncProgress>? progress)
    {
        lock (_progressGate) _progress = value;
        progress?.Report(value);
        ProgressChanged?.Invoke(this, new SyncProgressEventArgs(value));
    }

    private void RaiseStatusChanged()
    {
        EventHandler<SyncStatusChangedEventArgs>? handler = StatusChanged;
        if (handler is null) return;
        handler(this, new SyncStatusChangedEventArgs(new SyncStatus(CurrentActivity, [], CurrentProgress)));
    }

    private ScopeSettings SettingsOf(SyncScopeDefinition scope) => new(
        scope.Mode ?? _options.DefaultMode,
        scope.ConflictStrategy ?? _options.DefaultConflictStrategy,
        scope.DeletionPolicy ?? _options.DefaultDeletionPolicy);

    private Dictionary<string, SyncScopeDefinition> BuildScopes(IEnumerable<SyncScopeDefinition> scopes)
    {
        var map = new Dictionary<string, SyncScopeDefinition>(StringComparer.Ordinal);
        foreach (SyncScopeDefinition scope in scopes)
        {
            if (!map.TryAdd(scope.ScopeId, scope)) throw new SyncConfigurationException($"The synchronization scope '{scope.ScopeId}' is registered twice.");
            ScopeSettings settings = SettingsOf(scope);
            SyncValidator.ValidateScope(scope, settings.Mode, settings.Strategy, _customResolver is not null);
        }

        return map;
    }

    private SyncResult Aggregate(IReadOnlyList<SyncResult> results, DateTimeOffset started)
    {
        SyncResultStatus status = results.Count == 0 ? SyncResultStatus.NoChanges
            : results.Any(r => r.Status == SyncResultStatus.AuthenticationRequired) ? SyncResultStatus.AuthenticationRequired
            : results.All(r => r.Status == SyncResultStatus.NoChanges) ? SyncResultStatus.NoChanges
            : results.All(r => r.Status is SyncResultStatus.Succeeded or SyncResultStatus.NoChanges) ? SyncResultStatus.Succeeded
            : results.Any(r => r.Status == SyncResultStatus.Canceled) ? SyncResultStatus.Canceled
            : results.All(r => r.Status == SyncResultStatus.Failed) ? SyncResultStatus.Failed
            : SyncResultStatus.PartiallySucceeded;

        return new SyncResult
        {
            Status = status,
            SessionId = string.Join(",", results.Select(r => r.SessionId)),
            StartedUtc = started,
            CompletedUtc = _time.GetUtcNow(),
            UploadedCount = results.Sum(r => r.UploadedCount),
            DownloadedCount = results.Sum(r => r.DownloadedCount),
            DeletedCount = results.Sum(r => r.DeletedCount),
            SkippedCount = results.Sum(r => r.SkippedCount),
            TransferredBytes = results.Sum(r => r.TransferredBytes),
            Conflicts = results.SelectMany(r => r.Conflicts).ToList(),
            Failures = results.SelectMany(r => r.Failures).ToList(),
            Operations = results.SelectMany(r => r.Operations).ToList(),
            ScopeResults = results,
        };
    }
}
