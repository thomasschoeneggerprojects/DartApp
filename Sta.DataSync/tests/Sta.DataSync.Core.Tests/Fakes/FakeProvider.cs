using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

namespace Sta.DataSync.Core.Tests.Fakes;

/// <summary>
/// In-memory destination shared by several simulated devices. Server creation times strictly increase, like a real
/// service clock. Supports fault injection and an optional change feed.
/// </summary>
internal sealed class FakeProvider : ISyncProvider, IRemoteChangeFeed
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Stored> _items = new(StringComparer.Ordinal);
    private readonly List<(long Sequence, string Path)> _changes = [];
    private long _sequence;
    private long _clock;

    public FakeProvider(SyncProviderCapabilities capabilities = SyncProviderCapabilities.ServerCreationOrder | SyncProviderCapabilities.ContentHashes)
    {
        Capabilities = capabilities;
    }

    public string ProviderId => "fake";

    public SyncProviderCapabilities Capabilities { get; set; }

    /// <summary>Called before each operation ("list", "get", "download", "upload", "delete") with the path; may throw.</summary>
    public Func<string, string, Exception?>? Fault { get; set; }

    /// <summary>Called before an upload is stored (e.g. to inject a competing write).</summary>
    public Action<RemoteUpload>? BeforeUpload { get; set; }

    /// <summary>Replaces downloaded bytes (corruption tests).</summary>
    public Func<string, byte[], byte[]>? TamperDownload { get; set; }

    public ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

    public int Count(string operation) => Calls.GetValueOrDefault(operation);

    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_gate) return _items.Keys.Order(StringComparer.Ordinal).ToList();
        }
    }

    public byte[] Read(string path)
    {
        lock (_gate) return _items[path].Content.ToArray();
    }

    /// <summary>Stores a file directly, optionally with a chosen creation time (to simulate concurrent writers).</summary>
    public RemoteSyncItem Put(string path, byte[] content, DateTimeOffset? created = null)
    {
        lock (_gate)
        {
            var stored = new Stored(Guid.NewGuid().ToString("N"), content, 1, created ?? NextTime(), NextTime());
            _items[path] = stored;
            RecordChange(path);
            return ToRemote(path, stored);
        }
    }

    public DateTimeOffset NextTime()
    {
        lock (_gate) return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++_clock);
    }

    public Task<IReadOnlyList<RemoteSyncItem>> ListItemsAsync(string pathPrefix, CancellationToken cancellationToken = default)
    {
        Before("list", pathPrefix);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<RemoteSyncItem>>(_items
                .Where(kv => kv.Key.StartsWith(pathPrefix, StringComparison.Ordinal))
                .Select(kv => ToRemote(kv.Key, kv.Value))
                .ToList());
        }
    }

    public Task<RemoteSyncItem?> GetItemAsync(string path, CancellationToken cancellationToken = default)
    {
        Before("get", path);
        lock (_gate) return Task.FromResult(_items.TryGetValue(path, out Stored? s) ? ToRemote(path, s) : null);
    }

    public Task<Stream> DownloadAsync(RemoteSyncItem item, CancellationToken cancellationToken = default)
    {
        Before("download", item.Path);
        byte[] content;
        lock (_gate)
        {
            if (!_items.TryGetValue(item.Path, out Stored? stored)) throw new SyncItemNotFoundException(item.Path);
            content = stored.Content.ToArray();
        }

        if (TamperDownload is not null) content = TamperDownload(item.Path, content);
        return Task.FromResult<Stream>(new MemoryStream(content));
    }

    public async Task<RemoteSyncItem> UploadAsync(RemoteUpload upload, Stream content, CancellationToken cancellationToken = default)
    {
        Before("upload", upload.Path);
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        BeforeUpload?.Invoke(upload);
        lock (_gate)
        {
            bool exists = _items.TryGetValue(upload.Path, out Stored? current);
            if (upload.ExpectedVersion is { } expected)
            {
                if (!Capabilities.HasFlag(SyncProviderCapabilities.ConditionalWrites)) throw new InvalidOperationException("Conditional write without capability.");
                bool ok = expected == RemoteUpload.CreateOnly ? !exists : exists && current!.Version.ToString(CultureInfo.InvariantCulture) == expected;
                if (!ok) throw new SyncConcurrencyException($"Precondition failed for {upload.Path}.");
            }

            var stored = exists
                ? current! with { Content = buffer.ToArray(), Version = current.Version + 1, Modified = NextTime() }
                : new Stored(Guid.NewGuid().ToString("N"), buffer.ToArray(), 1, NextTime(), NextTime());
            _items[upload.Path] = stored;
            RecordChange(upload.Path);
            return ToRemote(upload.Path, stored);
        }
    }

    public Task DeleteAsync(RemoteSyncItem item, string? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        Before("delete", item.Path);
        lock (_gate)
        {
            if (_items.Remove(item.Path)) RecordChange(item.Path);
        }

        return Task.CompletedTask;
    }

    public Task<string> GetStartTokenAsync(CancellationToken cancellationToken = default)
    {
        Before("token", string.Empty);
        lock (_gate) return Task.FromResult(_sequence.ToString(CultureInfo.InvariantCulture));
    }

    public Task<RemoteChangeSet> GetChangesAsync(string token, string pathPrefix, CancellationToken cancellationToken = default)
    {
        Before("changes", pathPrefix);
        long since = long.Parse(token, CultureInfo.InvariantCulture);
        lock (_gate)
        {
            bool changed = _changes.Any(c => c.Sequence > since && c.Path.StartsWith(pathPrefix, StringComparison.Ordinal));
            return Task.FromResult(new RemoteChangeSet(changed, _sequence.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private void Before(string operation, string path)
    {
        Calls.AddOrUpdate(operation, 1, (_, n) => n + 1);
        if (Fault?.Invoke(operation, path) is { } exception) throw exception;
    }

    private void RecordChange(string path) => _changes.Add((++_sequence, path));

    private static RemoteSyncItem ToRemote(string path, Stored stored)
        => new(path, stored.Id, stored.Version.ToString(CultureInfo.InvariantCulture), stored.Content.Length,
            ContentHash.Format(SHA256.HashData(stored.Content)), stored.Created, stored.Modified);

    private sealed record Stored(string Id, byte[] Content, long Version, DateTimeOffset Created, DateTimeOffset Modified);
}
