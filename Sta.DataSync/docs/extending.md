# Extending Sta.DataSync

## A new destination provider

A provider is a blob store for logical paths. Implement `ISyncProvider` (and `IRemoteChangeFeed` if the service offers
incremental change detection), declare honest capabilities, map errors, register it. Nothing in the engine, the data
sources, conflict handling or the application changes.

```csharp
public sealed class WebDavSyncProvider(HttpClient http, IOptions<WebDavOptions> options) : ISyncProvider
{
    public string ProviderId => "webdav";

    // WebDAV supports If-Match / If-None-Match: the engine then uses compare-and-swap on one manifest file.
    public SyncProviderCapabilities Capabilities => SyncProviderCapabilities.ConditionalWrites;

    public Task<IReadOnlyList<RemoteSyncItem>> ListItemsAsync(string pathPrefix, CancellationToken ct = default) => /* PROPFIND, all pages */;
    public Task<RemoteSyncItem?> GetItemAsync(string path, CancellationToken ct = default) => /* PROPFIND depth 0 */;
    public Task<Stream> DownloadAsync(RemoteSyncItem item, CancellationToken ct = default) => /* GET, streamed */;
    public Task<RemoteSyncItem> UploadAsync(RemoteUpload upload, Stream content, CancellationToken ct = default) =>
        /* PUT; ExpectedVersion -> If-Match (RemoteUpload.CreateOnly -> If-None-Match: *); 412 -> SyncConcurrencyException */;
    public Task DeleteAsync(RemoteSyncItem item, string? expectedVersion = null, CancellationToken ct = default) => /* DELETE, 404 ok */;
}

public static class WebDavServiceCollectionExtensions
{
    public static IServiceCollection AddWebDavSyncProvider(this IServiceCollection services, Action<WebDavOptions> configure)
    {
        services.AddOptions<WebDavOptions>().Configure(configure);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISyncProvider, WebDavSyncProvider>());
        return services;
    }
}
```

Checklist:

- [ ] `ConditionalWrites` only if uploads and deletes are truly atomic compare-and-swap; otherwise `ServerCreationOrder`
      only if server-assigned creation times give one consistent order. With neither, the engine refuses the provider.
- [ ] `UploadAsync` replaces an existing item at the same path (no duplicates).
- [ ] Deleting a missing item succeeds.
- [ ] Listings return all pages; `RemoteSyncItem.CreatedUtc` is the server time.
- [ ] Error mapping: transient → `SyncTransientException` (with `RetryAfter` when known), credentials →
      `SyncAuthenticationException`, failed precondition → `SyncConcurrencyException`, missing → `SyncItemNotFoundException`,
      other permanent errors → `SyncProviderException`. Never put tokens or content in messages.
- [ ] Streams: never buffer whole files.
- [ ] Reuse the engine test fakes' scenarios against your provider (see `tests/Sta.DataSync.IntegrationTests`).

Use several providers side by side by giving every scope a `ProviderId`.

## A new data source

Implement `ISyncDataSource`:

- `GetItemsAsync`: stable ids (`SyncPath` rules), `Length`, a cheap `Fingerprint` (enables hash caching).
- `OpenReadAsync`: streaming reads.
- `ApplyChangeAsync`: write atomically (temporary + replace), read the content stream to its end before replacing
  (it throws `SyncIntegrityException` on a hash mismatch), honor `ExpectNotExists` / `ExpectedCurrentFingerprint`
  with `SyncConcurrencyException`.
- `Capabilities`: `Write`, `Delete`, `CreateItems` (allows conflict copies).
- Optional: `ISyncSnapshotSource` for consistent session views, `ISyncChangeTracking` to trigger synchronization.

Register with `services.AddSyncSource("id", sp => new MySource(...), new SyncScopeOptions { Mode = ... })`.

## Conflict resolvers

```csharp
public sealed class SettingsResolver : IConflictResolver
{
    public Task<ConflictResolution> ResolveAsync(SyncConflict conflict, CancellationToken ct = default)
        => Task.FromResult(conflict.ItemId.EndsWith(".cache", StringComparison.Ordinal) ? ConflictResolution.KeepRemote : ConflictResolution.KeepBoth);
}
services.AddConflictResolver<SettingsResolver>();   // and ConflictStrategy = ConflictResolutionStrategy.Custom
```

The engine contains no merge rules; resolvers decide per item and may return `Defer`.

## Content transforms (future encryption)

`ISyncContentTransform` wraps content on upload and unwraps it after download, independent of the provider. Set it on
a `SyncScopeDefinition.ContentTransform`. The manifest records the transform id per item; hashes are always computed on
plain content, and objects with a transform are never deduplicated by length. **Version 1 ships no transform:
content is stored as-is (protected by Google's transport and storage encryption only).** An encryption transform must
use a vetted algorithm (e.g. AES-GCM from `System.Security.Cryptography`) with keys from platform secure storage; do
not implement custom cryptography.

## Record-level synchronization (future)

Version 1 synchronizes SQLite at database level. The design leaves room for record-level sync without engine changes
to the contracts: a source that exposes each record as an item (`table/{guid}` ids, fingerprint = row version) gets
per-record change detection, tombstones and conflicts from the existing engine; record merge policies plug in as
`IConflictResolver`s. Prerequisites in the application schema: GUID primary keys, a row version or modification
counter, soft deletes (or a tombstone table).
