using System.Globalization;
using System.Text;

namespace Sta.DataSync.Core.Tests.Fakes;

/// <summary>Writable in-memory data source with fingerprints and fault hooks.</summary>
internal sealed class FakeSource(string sourceId = "data", SyncDataSourceCapabilities capabilities = SyncDataSourceCapabilities.Write | SyncDataSourceCapabilities.Delete | SyncDataSourceCapabilities.CreateItems)
    : ISyncDataSource
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Content, long Stamp, DateTimeOffset Modified)> _items = new(StringComparer.Ordinal);
    private long _stamp;
    private int _concurrentReads;

    public string SourceId => sourceId;

    public SyncDataSourceCapabilities Capabilities => capabilities;

    /// <summary>Called when an item is opened for reading (e.g. to modify it during the upload).</summary>
    public Action<string>? OnOpenRead { get; set; }

    public int MaxConcurrentEnumerations { get; private set; }

    public void Set(string id, string text, DateTimeOffset? modified = null)
    {
        lock (_gate) _items[id] = (Encoding.UTF8.GetBytes(text), ++_stamp, modified ?? DateTimeOffset.UnixEpoch.AddSeconds(_stamp));
    }

    public void Remove(string id)
    {
        lock (_gate) _items.Remove(id);
    }

    public string? Get(string id)
    {
        lock (_gate) return _items.TryGetValue(id, out var item) ? Encoding.UTF8.GetString(item.Content) : null;
    }

    public IReadOnlyList<string> Ids
    {
        get
        {
            lock (_gate) return _items.Keys.Order(StringComparer.Ordinal).ToList();
        }
    }

    public async Task<IReadOnlyList<SyncItem>> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        int current = Interlocked.Increment(ref _concurrentReads);
        MaxConcurrentEnumerations = Math.Max(MaxConcurrentEnumerations, current);
        try
        {
            await Task.Delay(5, cancellationToken);
            lock (_gate)
            {
                return _items.Select(kv => new SyncItem(kv.Key, kv.Value.Content.Length, kv.Value.Modified, kv.Value.Stamp.ToString(CultureInfo.InvariantCulture))).ToList();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentReads);
        }
    }

    public Task<Stream> OpenReadAsync(string itemId, CancellationToken cancellationToken = default)
    {
        byte[] content;
        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var item)) throw new SyncItemNotFoundException(itemId);
            content = item.Content;
        }

        OnOpenRead?.Invoke(itemId);
        return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    public async Task ApplyChangeAsync(SyncChange change, CancellationToken cancellationToken = default)
    {
        if (change.Kind == SyncChangeKind.Write)
        {
            // Read everything first: the verifying stream throws at its end if the content is corrupt.
            var buffer = new MemoryStream();
            await change.Content!.CopyToAsync(buffer, cancellationToken);
            lock (_gate)
            {
                CheckExpected(change);
                _items[change.ItemId] = (buffer.ToArray(), ++_stamp, DateTimeOffset.UnixEpoch.AddSeconds(_stamp));
            }

            return;
        }

        lock (_gate)
        {
            CheckExpected(change);
            _items.Remove(change.ItemId);
        }
    }

    private void CheckExpected(SyncChange change)
    {
        bool exists = _items.TryGetValue(change.ItemId, out var current);
        if (change.ExpectNotExists && exists) throw new SyncConcurrencyException($"{change.ItemId} was created meanwhile.");
        if (change.ExpectedCurrentFingerprint is { } fingerprint && (!exists || current.Stamp.ToString(CultureInfo.InvariantCulture) != fingerprint))
        {
            throw new SyncConcurrencyException($"{change.ItemId} changed meanwhile.");
        }
    }
}

/// <summary>Thread-safe in-memory state store with a fault hook for crash simulations.</summary>
internal sealed class FakeStateStore : ISyncStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ScopeSyncState> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), ItemSyncState> _items = [];
    private readonly Dictionary<(string, string), (SyncConflict Conflict, ConflictResolution? Resolution)> _conflicts = [];
    private readonly Dictionary<string, SyncJournalEntry> _journal = new(StringComparer.Ordinal);

    public string DeviceId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Throws instead of committing when it returns an exception.</summary>
    public Func<SyncStateCommit, Exception?>? CommitFault { get; set; }

    public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(DeviceId);

    public Task<ScopeSyncState?> GetScopeAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(_scopes.GetValueOrDefault(scopeId));
    }

    public Task<IReadOnlyDictionary<string, ItemSyncState>> GetItemsAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, ItemSyncState>>(
                _items.Where(kv => kv.Key.Item1 == scopeId).ToDictionary(kv => kv.Key.Item2, kv => kv.Value, StringComparer.Ordinal));
        }
    }

    public Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(string? scopeId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SyncConflict>>(
                _conflicts.Where(kv => scopeId is null || kv.Key.Item1 == scopeId).Select(kv => kv.Value.Conflict).ToList());
        }
    }

    public Task<IReadOnlyDictionary<string, ConflictResolution>> GetConflictResolutionsAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, ConflictResolution>>(_conflicts
                .Where(kv => kv.Key.Item1 == scopeId && kv.Value.Resolution is not null)
                .ToDictionary(kv => kv.Key.Item2, kv => kv.Value.Resolution!, StringComparer.Ordinal));
        }
    }

    public Task SetConflictResolutionAsync(string scopeId, string itemId, ConflictResolution resolution, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_conflicts.TryGetValue((scopeId, itemId), out var stored)) throw new SyncItemNotFoundException(itemId);
            _conflicts[(scopeId, itemId)] = (stored.Conflict, resolution);
        }

        return Task.CompletedTask;
    }

    public Task<SyncJournalEntry?> GetOpenJournalAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(_journal.Values.Where(e => e.ScopeId == scopeId).OrderByDescending(e => e.UpdatedUtc).FirstOrDefault());
    }

    public Task WriteJournalAsync(SyncJournalEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate) _journal[entry.SessionId] = entry;
        return Task.CompletedTask;
    }

    public Task DeleteJournalAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate) _journal.Remove(sessionId);
        return Task.CompletedTask;
    }

    public IReadOnlyList<SyncJournalEntry> Journal
    {
        get
        {
            lock (_gate) return _journal.Values.ToList();
        }
    }

    public Task CommitAsync(SyncStateCommit commit, CancellationToken cancellationToken = default)
    {
        if (CommitFault?.Invoke(commit) is { } fault) throw fault;
        lock (_gate)
        {
            if (commit.Scope is not null) _scopes[commit.ScopeId] = commit.Scope;
            foreach (ItemSyncState item in commit.UpsertItems) _items[(commit.ScopeId, item.ItemId)] = item;
            foreach (string id in commit.RemoveItems) _items.Remove((commit.ScopeId, id));
            foreach (SyncConflict conflict in commit.UpsertConflicts) _conflicts[(commit.ScopeId, conflict.ItemId)] = (conflict, null);
            foreach (string id in commit.RemoveConflicts) _conflicts.Remove((commit.ScopeId, id));
            if (commit.CompleteJournalSession is not null) _journal.Remove(commit.CompleteJournalSession);
        }

        return Task.CompletedTask;
    }

    public Task ResetScopeAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _scopes.Remove(scopeId);
            foreach (var key in _items.Keys.Where(k => k.Item1 == scopeId).ToList()) _items.Remove(key);
            foreach (var key in _conflicts.Keys.Where(k => k.Item1 == scopeId).ToList()) _conflicts.Remove(key);
        }

        return Task.CompletedTask;
    }
}
