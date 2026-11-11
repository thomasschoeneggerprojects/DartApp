using System.Collections.Concurrent;

namespace Sta.DataSync.Engine;

/// <summary>
/// Serializes runs per scope. Different scopes run independently. While one run of a scope is active, at most one
/// further run is queued; later requests share that queued run (bounded, and it still starts after their changes).
/// </summary>
internal sealed class SyncCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, ScopeGate> _gates = new(StringComparer.Ordinal);

    public Task<SyncResult> RunAsync(string scopeId, Func<CancellationToken, Task<SyncResult>> run, CancellationToken cancellationToken)
    {
        ScopeGate gate = _gates.GetOrAdd(scopeId, _ => new ScopeGate());
        TaskCompletionSource<SyncResult> queued;
        lock (gate)
        {
            if (gate.Queued is { } existing) return existing.Task.WaitAsync(cancellationToken);
            queued = new TaskCompletionSource<SyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.Queued = queued;
        }

        _ = RunQueuedAsync(gate, queued, run, cancellationToken);
        return queued.Task;
    }

    public bool IsRunning(string scopeId) => _gates.TryGetValue(scopeId, out ScopeGate? gate) && gate.Semaphore.CurrentCount == 0;

    public void Dispose()
    {
        foreach (ScopeGate gate in _gates.Values) gate.Semaphore.Dispose();
    }

    private static async Task RunQueuedAsync(ScopeGate gate, TaskCompletionSource<SyncResult> queued, Func<CancellationToken, Task<SyncResult>> run, CancellationToken cancellationToken)
    {
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            lock (gate)
            {
                if (gate.Queued == queued) gate.Queued = null;
            }

            queued.TrySetCanceled(ex.CancellationToken);
            return;
        }

        try
        {
            lock (gate)
            {
                if (gate.Queued == queued) gate.Queued = null;
            }

            queued.TrySetResult(await run(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException ex)
        {
            queued.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            queued.TrySetException(ex);
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }

    private sealed class ScopeGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public TaskCompletionSource<SyncResult>? Queued { get; set; }
    }
}
