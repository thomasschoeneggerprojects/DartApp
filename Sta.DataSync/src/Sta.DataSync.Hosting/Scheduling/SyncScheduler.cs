using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Sta.DataSync.Hosting;

/// <summary>Controls automatic synchronization. Foreground-oriented: platform layers pause it in the background.</summary>
public interface ISyncScheduler
{
    /// <summary><see langword="true"/> after <see cref="StartAsync"/> until <see cref="StopAsync"/>.</summary>
    bool IsRunning { get; }

    /// <summary><see langword="true"/> while automatic synchronization is paused.</summary>
    bool IsPaused { get; }

    /// <summary>Starts automatic synchronization (runs a startup sync when configured).</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops automatic synchronization and cancels a running automatic sync.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Pauses automatic synchronization (e.g. app in background or user turned auto sync off).</summary>
    void PauseAutomaticSync();

    /// <summary>Resumes automatic synchronization; synchronizes soon if something is pending.</summary>
    void ResumeAutomaticSync();

    /// <summary>Reports a local change; synchronization follows after the debounce time.</summary>
    void NotifyLocalChange(string? scopeId = null);

    /// <summary>Reports that the user signed in again; re-enables automatic synchronization after an authentication failure.</summary>
    void NotifyAuthenticationChanged();

    /// <summary>Synchronizes now (manual), regardless of pause and network restrictions.</summary>
    Task<SyncResult> SyncNowAsync(IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default scheduler: one background loop with explicit due times (startup, periodic, debounced local changes,
/// connectivity restored, transient-failure retries with backoff and jitter). It never queues more than one pending run.
/// Authentication failures stop automatic runs until <see cref="NotifyAuthenticationChanged"/>; permanent failures are
/// not retried until the next regular trigger.
/// </summary>
public sealed partial class SyncScheduler : ISyncScheduler, IAsyncDisposable
{
    private readonly IDataSyncService _service;
    private readonly SyncScheduleOptions _options;
    private readonly IConnectivityMonitor? _connectivity;
    private readonly SyncActivityState _activity;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly IReadOnlyList<ISyncChangeTracking> _trackedSources;
    private TaskCompletionSource _wake = NewSignal();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private DateTimeOffset? _changeDue;
    private DateTimeOffset? _retryDue;
    private DateTimeOffset? _periodicDue;
    private bool _immediate;
    private bool _paused;
    private bool _authenticationBlocked;
    private bool _wasOffline;
    private int _retryAttempt;

    /// <summary>Creates the scheduler.</summary>
    public SyncScheduler(
        IDataSyncService service,
        IOptions<SyncScheduleOptions> options,
        IEnumerable<SyncScopeDefinition> scopes,
        SyncActivityState activity,
        IConnectivityMonitor? connectivity = null,
        TimeProvider? timeProvider = null,
        ILogger<SyncScheduler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopes);
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _options = options.Value;
        IReadOnlyList<string> errors = _options.Validate();
        if (errors.Count > 0) throw new SyncConfigurationException("Invalid SyncScheduleOptions: " + string.Join(" ", errors));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _connectivity = connectivity;
        _time = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _trackedSources = scopes.Select(s => s.Source).OfType<ISyncChangeTracking>().Distinct().ToList();
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_gate) return _loop is not null;
        }
    }

    /// <inheritdoc />
    public bool IsPaused
    {
        get
        {
            lock (_gate) return _paused;
        }
    }

    /// <summary>When the next retry after a transient failure is due (diagnostics and tests).</summary>
    internal DateTimeOffset? PendingRetryUtc
    {
        get
        {
            lock (_gate) return _retryDue;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_loop is not null) return Task.CompletedTask;
            _stop = new CancellationTokenSource();
            _immediate = _options.SyncOnStartup;
            _periodicDue = NextPeriodic();
            _wasOffline = !IsConnected;
            _activity.IsOffline = _wasOffline;
            _loop = Task.Run(() => RunLoopAsync(_stop.Token), CancellationToken.None);
        }

        if (_connectivity is not null) _connectivity.ConnectivityChanged += OnConnectivityChanged;
        foreach (ISyncChangeTracking source in _trackedSources) source.Changed += OnSourceChanged;
        LogStarted(_logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loop;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            loop = _loop;
            stop = _stop;
            _loop = null;
            _stop = null;
        }

        if (loop is null) return;
        if (_connectivity is not null) _connectivity.ConnectivityChanged -= OnConnectivityChanged;
        foreach (ISyncChangeTracking source in _trackedSources) source.Changed -= OnSourceChanged;
        await stop!.CancelAsync().ConfigureAwait(false);
        Wake();
        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Loop ended by the stop token.
        }
        finally
        {
            stop.Dispose();
        }
    }

    /// <inheritdoc />
    public void PauseAutomaticSync()
    {
        lock (_gate) _paused = true;
        _activity.IsPaused = true;
        LogPaused(_logger, true);
    }

    /// <inheritdoc />
    public void ResumeAutomaticSync()
    {
        lock (_gate)
        {
            _paused = false;
            // Catch up on anything that became due while paused.
            if (_periodicDue is { } due && due <= _time.GetUtcNow()) _immediate = true;
        }

        _activity.IsPaused = false;
        LogPaused(_logger, false);
        Wake();
    }

    /// <inheritdoc />
    public void NotifyLocalChange(string? scopeId = null)
    {
        if (!_options.SyncOnLocalChanges) return;
        lock (_gate) _changeDue = _time.GetUtcNow() + _options.ChangeDebounce;
        Wake();
    }

    /// <inheritdoc />
    public void NotifyAuthenticationChanged()
    {
        lock (_gate)
        {
            _authenticationBlocked = false;
            _immediate = true;
        }

        Wake();
    }

    /// <inheritdoc />
    public async Task<SyncResult> SyncNowAsync(IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        SyncResult result = await _service.SynchronizeAsync(null, progress, cancellationToken).ConfigureAwait(false);
        Record(result);
        return result;
    }

    /// <summary>Stops the scheduler.</summary>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private bool IsConnected => _connectivity?.IsConnected ?? true;

    private bool NetworkAllowed => IsConnected && (!_options.WifiOnly || (_connectivity?.IsUnmetered ?? true));

    private async Task RunLoopAsync(CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            (bool runNow, DateTimeOffset? next, Task wake) = Evaluate();
            if (runNow)
            {
                await RunAutomaticAsync(stopToken).ConfigureAwait(false);
                continue;
            }

            await WaitAsync(wake, next, stopToken).ConfigureAwait(false);
        }
    }

    /// <summary>Decides whether an automatic run is due now; otherwise returns when to look again.</summary>
    private (bool RunNow, DateTimeOffset? Next, Task Wake) Evaluate()
    {
        lock (_gate)
        {
            Task wake = _wake.Task;
            if (_paused || _authenticationBlocked || !NetworkAllowed) return (false, null, wake);

            DateTimeOffset now = _time.GetUtcNow();
            DateTimeOffset? next = Earliest(_changeDue, _retryDue, _periodicDue);
            bool due = _immediate || (next is { } n && n <= now);
            if (!due) return (false, next, wake);

            _immediate = false;
            _changeDue = null;
            _retryDue = null;
            _periodicDue = NextPeriodic();
            return (true, null, wake);
        }
    }

    private async Task RunAutomaticAsync(CancellationToken stopToken)
    {
        try
        {
            SyncResult result = await _service.SynchronizeAsync(null, null, stopToken).ConfigureAwait(false);
            Record(result);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (SyncConfigurationException ex)
        {
            LogRunFailed(_logger, ex.Message, ex);
            lock (_gate) _authenticationBlocked = true;
        }
    }

    /// <summary>Updates retry and authentication state from a result.</summary>
    private void Record(SyncResult result)
    {
        lock (_gate)
        {
            if (result.Status == SyncResultStatus.AuthenticationRequired)
            {
                _authenticationBlocked = true;
                _retryDue = null;
                return;
            }

            bool transient = result.Status is SyncResultStatus.Failed or SyncResultStatus.PartiallySucceeded
                && result.Failures.Any(f => f.IsTransient);
            if (!transient)
            {
                _retryAttempt = 0;
                _retryDue = null;
                return;
            }

            _retryAttempt++;
            TimeSpan delay = RetryDelay(_retryAttempt);
            _retryDue = _time.GetUtcNow() + delay;
            LogRetryScheduled(_logger, delay.TotalSeconds, _retryAttempt);
        }
    }

    private TimeSpan RetryDelay(int attempt)
    {
        double exponential = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 16));
        double capped = Math.Min(exponential, _options.RetryMaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped * (0.5 + (RandomNumberGenerator.GetInt32(0, 1001) / 2000.0)));
    }

    private async Task WaitAsync(Task wake, DateTimeOffset? next, CancellationToken stopToken)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        Task delay = next is { } due
            ? Task.Delay(Max(due - _time.GetUtcNow(), TimeSpan.Zero), _time, delayCts.Token)
            : Task.Delay(Timeout.InfiniteTimeSpan, _time, delayCts.Token);
        await Task.WhenAny(wake, delay).ConfigureAwait(false);
        await delayCts.CancelAsync().ConfigureAwait(false);
    }

    private void OnSourceChanged(object? sender, SyncSourceChangedEventArgs e) => NotifyLocalChange(e.SourceId);

    private void OnConnectivityChanged(object? sender, EventArgs e)
    {
        bool connected = IsConnected;
        _activity.IsOffline = !connected;
        lock (_gate)
        {
            if (connected && _wasOffline && _options.SyncOnConnectivityRestored) _immediate = true;
            _wasOffline = !connected;
        }

        LogConnectivity(_logger, connected);
        Wake();
    }

    private DateTimeOffset? NextPeriodic() => _options.Interval > TimeSpan.Zero ? _time.GetUtcNow() + _options.Interval : null;

    private void Wake()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _wake;
            _wake = NewSignal();
        }

        previous.TrySetResult();
    }

    private static DateTimeOffset? Earliest(params DateTimeOffset?[] values) => values.Where(v => v is not null).Min();

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Automatic synchronization started.")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "Automatic synchronization paused: {Paused}.")]
    private static partial void LogPaused(ILogger logger, bool paused);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Information, Message = "Synchronization retry scheduled in {DelaySeconds:0}s (attempt {Attempt}).")]
    private static partial void LogRetryScheduled(ILogger logger, double delaySeconds, int attempt);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Information, Message = "Connectivity changed: connected = {Connected}.")]
    private static partial void LogConnectivity(ILogger logger, bool connected);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Error, Message = "Automatic synchronization stopped: {Reason}")]
    private static partial void LogRunFailed(ILogger logger, string reason, Exception exception);
}
