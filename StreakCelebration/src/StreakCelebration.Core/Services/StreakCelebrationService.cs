using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreakCelebration.Animations;
using StreakCelebration.Models;

namespace StreakCelebration.Services;

/// <summary>
/// Queue that plays celebrations one at a time on the most recently registered available host.
/// See <see cref="IStreakCelebrationService"/> for the cancellation, navigation and backgrounding rules.
/// </summary>
public sealed class StreakCelebrationService : IStreakCelebrationService, IStreakCelebrationHostRegistry, IDisposable
{
    private readonly object _gate = new();
    private readonly LinkedList<Request> _pending = new();
    private readonly List<IStreakCelebrationHost> _hosts = [];
    private readonly StreakCelebrationOptions _options;
    private readonly ICelebrationDispatcher _dispatcher;
    private readonly IReducedMotionProvider _reducedMotion;
    private readonly ICelebrationSoundPlayer _sound;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly EventHandler _hostAvailabilityChanged;

    private TaskCompletionSource _wake = NewSignal();
    private Task? _pump;
    private Playback? _active;
    private bool _disposed;

    /// <summary>Creates the service. <paramref name="options"/> are validated and copied.</summary>
    public StreakCelebrationService(
        StreakCelebrationOptions options,
        ICelebrationDispatcher? dispatcher = null,
        IReducedMotionProvider? reducedMotion = null,
        ICelebrationSoundPlayer? sound = null,
        TimeProvider? timeProvider = null,
        ILogger<StreakCelebrationService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ThrowIfInvalid();
        _options = options.Clone();
        _dispatcher = dispatcher ?? new InlineCelebrationDispatcher();
        _reducedMotion = reducedMotion ?? new NoReducedMotionProvider();
        _sound = sound ?? new SilentCelebrationSoundPlayer();
        _time = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _hostAvailabilityChanged = (_, _) => Wake();
    }

    /// <summary>The validated options snapshot in use.</summary>
    public StreakCelebrationOptions Options => _options;

    /// <inheritdoc />
    public bool IsShowing
    {
        get { lock (_gate) return _active is not null; }
    }

    /// <inheritdoc />
    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>Registered hosts (for diagnostics and tests).</summary>
    public int HostCount
    {
        get { lock (_gate) return _hosts.Count; }
    }

    /// <inheritdoc />
    public Task<StreakCelebrationResult> ShowAsync(StreakCelebrationData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.ThrowIfInvalid();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<StreakCelebrationResult>(cancellationToken);

        var request = new Request(data, cancellationToken, _time.GetUtcNow());
        Request? dropped = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending.Count >= _options.MaxQueueLength)
            {
                dropped = _pending.First!.Value;
                RemovePending(dropped);
            }

            request.Node = _pending.AddLast(request);
            _pump ??= Task.Run(PumpAsync);
            WakeNoLock();
        }

        dropped?.Complete(StreakCelebrationResult.Dropped);
        request.ListenForCancellation(this);
        return request.Task;
    }

    /// <inheritdoc />
    public bool DismissCurrent()
    {
        Playback? active;
        lock (_gate) active = _active;
        return active?.Stop(StopReason.Dismissed) ?? false;
    }

    /// <inheritdoc />
    public void ClearQueue()
    {
        List<Request> removed;
        lock (_gate)
        {
            removed = [.. _pending];
            foreach (Request request in removed) RemovePending(request);
            WakeNoLock();
        }

        foreach (Request request in removed) request.Complete(StreakCelebrationResult.Dropped);
    }

    /// <inheritdoc />
    public IDisposable RegisterHost(IStreakCelebrationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Re-registering moves the host to the top instead of adding it twice.
            if (_hosts.Remove(host)) host.AvailabilityChanged -= _hostAvailabilityChanged;
            _hosts.Add(host);
            host.AvailabilityChanged += _hostAvailabilityChanged;
            WakeNoLock();
        }

        return new HostRegistration(this, host);
    }

    /// <summary>Stops the running celebration (Interrupted) and drops all waiting requests.</summary>
    public void Dispose()
    {
        List<Request> removed;
        Playback? active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            removed = [.. _pending];
            foreach (Request request in removed) RemovePending(request);
            foreach (IStreakCelebrationHost host in _hosts) host.AvailabilityChanged -= _hostAvailabilityChanged;
            _hosts.Clear();
            active = _active;
            WakeNoLock();
        }

        active?.Stop(StopReason.Interrupted);
        _lifetime.Cancel();
        foreach (Request request in removed) request.Complete(StreakCelebrationResult.Dropped);
    }

    private void UnregisterHost(IStreakCelebrationHost host)
    {
        Playback? interrupted = null;
        lock (_gate)
        {
            if (!_hosts.Remove(host)) return;
            host.AvailabilityChanged -= _hostAvailabilityChanged;
            if (_active?.Host == host) interrupted = _active;
            WakeNoLock();
        }

        interrupted?.Stop(StopReason.Interrupted);
    }

    private void CancelPending(Request request)
    {
        bool removed;
        lock (_gate)
        {
            removed = request.Node is not null;
            if (removed)
            {
                RemovePending(request);
                WakeNoLock();
            }
        }

        // A running request is stopped through its linked playback token instead.
        if (removed) request.Cancel();
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            List<Request>? expired;
            Request? next = null;
            Playback? playback = null;
            Task wake;
            TimeSpan? untilExpiry = null;

            lock (_gate)
            {
                expired = RemoveExpiredNoLock();
                if (_pending.Count == 0 || _disposed)
                {
                    _pump = null;
                    wake = Task.CompletedTask;
                }
                else
                {
                    IStreakCelebrationHost? host = SelectHostNoLock();
                    wake = _wake.Task;
                    if (host is not null)
                    {
                        next = _pending.First!.Value;
                        RemovePending(next);
                        playback = new Playback(next, host, _lifetime.Token);
                        _active = playback;
                    }
                    else
                    {
                        untilExpiry = _pending.First!.Value.EnqueuedAt + _options.QueuedLifetime - _time.GetUtcNow();
                    }
                }
            }

            if (expired is not null)
            {
                foreach (Request request in expired) request.Complete(StreakCelebrationResult.Expired);
            }

            if (playback is not null)
            {
                await PlayAsync(playback).ConfigureAwait(false);
                continue;
            }

            if (untilExpiry is null) return;
            await WaitAsync(wake, untilExpiry.Value).ConfigureAwait(false);
        }
    }

    private async Task PlayAsync(Playback playback)
    {
        IStreakCelebrationHost host = playback.Host;
        Request request = playback.Request;
        EventHandler onAvailability = (_, _) =>
        {
            if (!host.IsAvailable) playback.Stop(StopReason.Interrupted);
        };

        StreakCelebrationResult result = StreakCelebrationResult.Interrupted;
        Exception? failure = null;
        bool cancelled = false;

        host.AvailabilityChanged += onAvailability;
        try
        {
            if (!host.IsAvailable) playback.Stop(StopReason.Interrupted);
            playback.Token.ThrowIfCancellationRequested();

            bool reduced = _options.RespectReducedMotion && IsReducedMotion();
            var plan = new CelebrationPlayback(request.Data, CelebrationTimeline.Create(request.Data, _options, reduced), _options);
            StartSound(request.Data.Level, playback.Token);

            StreakCelebrationResult played = await _dispatcher
                .InvokeAsync(() => host.PlayAsync(plan, playback.Token))
                .ConfigureAwait(false);
            result = playback.Reason == StopReason.Dismissed ? StreakCelebrationResult.Dismissed : played;
        }
        catch (OperationCanceledException)
        {
            cancelled = request.Token.IsCancellationRequested;
            result = playback.Reason == StopReason.Dismissed ? StreakCelebrationResult.Dismissed : StreakCelebrationResult.Interrupted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Streak celebration host {Host} failed.", host.GetType().Name);
            failure = ex;
        }
        finally
        {
            host.AvailabilityChanged -= onAvailability;
            lock (_gate) _active = null;
            playback.Dispose();
        }

        // Completed only after the playback was released, so callers never observe IsShowing for a finished celebration.
        if (cancelled) request.Cancel();
        else if (failure is not null) request.Fail(failure);
        else request.Complete(result);
    }

    private bool IsReducedMotion()
    {
        try
        {
            return _reducedMotion.IsReducedMotionEnabled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the reduced-motion setting failed; using full motion.");
            return false;
        }
    }

    private void StartSound(CelebrationLevel level, CancellationToken cancellationToken)
    {
        string? file = _options.SoundFor(level);
        if (file is null) return;
        _ = PlaySoundAsync(file, cancellationToken);
    }

    private async Task PlaySoundAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            await _sound.PlayAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Celebration ended before the sound started.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Celebration sound {SoundFile} could not be played.", file);
        }
    }

    private async Task WaitAsync(Task wake, TimeSpan untilExpiry)
    {
        if (untilExpiry < TimeSpan.Zero) untilExpiry = TimeSpan.Zero;
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Task delay = Task.Delay(untilExpiry, _time, delayCts.Token);
        await Task.WhenAny(wake, delay).ConfigureAwait(false);
        delayCts.Cancel();
    }

    private IStreakCelebrationHost? SelectHostNoLock()
    {
        for (int i = _hosts.Count - 1; i >= 0; i--)
        {
            if (_hosts[i].IsAvailable) return _hosts[i];
        }

        return null;
    }

    private List<Request>? RemoveExpiredNoLock()
    {
        List<Request>? expired = null;
        DateTimeOffset now = _time.GetUtcNow();
        LinkedListNode<Request>? node = _pending.First;
        while (node is not null)
        {
            LinkedListNode<Request>? following = node.Next;
            if (now - node.Value.EnqueuedAt >= _options.QueuedLifetime)
            {
                (expired ??= []).Add(node.Value);
                RemovePending(node.Value);
            }

            node = following;
        }

        return expired;
    }

    private void RemovePending(Request request)
    {
        if (request.Node is null) return;
        _pending.Remove(request.Node);
        request.Node = null;
    }

    private void Wake()
    {
        lock (_gate) WakeNoLock();
    }

    private void WakeNoLock()
    {
        TaskCompletionSource previous = _wake;
        _wake = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private enum StopReason
    {
        None,
        Dismissed,
        Interrupted,
    }

    private sealed class Request(StreakCelebrationData data, CancellationToken token, DateTimeOffset enqueuedAt)
    {
        private readonly TaskCompletionSource<StreakCelebrationResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;

        public StreakCelebrationData Data { get; } = data;

        public CancellationToken Token { get; } = token;

        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;

        /// <summary>Position in the waiting list; <see langword="null"/> once taken out (playing or finished).</summary>
        public LinkedListNode<Request>? Node { get; set; }

        public Task<StreakCelebrationResult> Task => _completion.Task;

        public void ListenForCancellation(StreakCelebrationService owner)
        {
            if (!Token.CanBeCanceled) return;
            _registration = Token.Register(static state =>
            {
                var (service, request) = ((StreakCelebrationService, Request))state!;
                service.CancelPending(request);
            }, (owner, this));
        }

        public void Complete(StreakCelebrationResult result)
        {
            _registration.Unregister();
            _completion.TrySetResult(result);
        }

        public void Cancel()
        {
            _registration.Unregister();
            _completion.TrySetCanceled(Token);
        }

        public void Fail(Exception exception)
        {
            _registration.Unregister();
            _completion.TrySetException(exception);
        }
    }

    private sealed class Playback : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private int _reason;

        public Playback(Request request, IStreakCelebrationHost host, CancellationToken lifetime)
        {
            Request = request;
            Host = host;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(request.Token, lifetime);
        }

        public Request Request { get; }

        public IStreakCelebrationHost Host { get; }

        public CancellationToken Token => _cts.Token;

        public StopReason Reason => (StopReason)Volatile.Read(ref _reason);

        /// <summary>First stop reason wins; returns <see langword="false"/> when already stopped.</summary>
        public bool Stop(StopReason reason)
        {
            if (Interlocked.CompareExchange(ref _reason, (int)reason, (int)StopReason.None) != (int)StopReason.None) return false;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Finished concurrently.
            }

            return true;
        }

        public void Dispose() => _cts.Dispose();
    }

    private sealed class HostRegistration(StreakCelebrationService owner, IStreakCelebrationHost host) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.UnregisterHost(host);
        }
    }
}
