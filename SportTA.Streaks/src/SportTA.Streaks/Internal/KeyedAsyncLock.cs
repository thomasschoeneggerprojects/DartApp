namespace SportTA.Streaks.Internal;

/// <summary>
/// Striped asynchronous mutual exclusion keyed by string. A fixed number of semaphores keeps memory bounded
/// regardless of how many keys exist; unrelated keys may occasionally share a stripe, which only serializes them.
/// </summary>
internal sealed class KeyedAsyncLock
{
    private readonly SemaphoreSlim[] _stripes;

    public KeyedAsyncLock(int stripeCount = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stripeCount, 1);
        _stripes = new SemaphoreSlim[stripeCount];
        for (int i = 0; i < stripeCount; i++)
        {
            _stripes[i] = new SemaphoreSlim(1, 1);
        }
    }

    public async Task<Releaser> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        uint hash = (uint)StringComparer.Ordinal.GetHashCode(key);
        SemaphoreSlim stripe = _stripes[hash % (uint)_stripes.Length];
        await stripe.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(stripe);
    }

    internal readonly struct Releaser : IDisposable
    {
        private readonly SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => _semaphore?.Release();
    }
}
