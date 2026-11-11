using Microsoft.Extensions.Logging;

namespace Sta.DataSync.Detection;

/// <summary>A local item with its content hash.</summary>
internal sealed record LocalItem(string Id, long Length, DateTimeOffset? ModifiedUtc, string? Fingerprint, string Hash);

/// <summary>Reads local item content (from a snapshot or directly from the source).</summary>
internal delegate Task<Stream> LocalReader(string itemId, CancellationToken cancellationToken);

/// <summary>
/// Enumerates local items and determines their hashes. Content is only read when the source gives no hash and the
/// fingerprint differs from the one recorded with the cached hash, so unchanged items cost no I/O.
/// </summary>
internal static partial class LocalChangeDetector
{
    public static async Task<LocalDetection> DetectAsync(
        IReadOnlyList<SyncItem> items,
        LocalReader reader,
        IReadOnlyDictionary<string, ItemSyncState> states,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, LocalItem>(items.Count, StringComparer.Ordinal);
        int hashed = 0;
        foreach (SyncItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SyncPath.IsValid(item.Id)) throw new SyncIntegrityException($"The data source returned an invalid item id '{item.Id}'.");

            string? hash = KnownHash(item, states);
            long length = item.Length;
            if (hash is null)
            {
                // The hashed length is authoritative: snapshot sources report the live file size, not the snapshot's.
                (hash, length) = await HashAsync(reader, item.Id, cancellationToken).ConfigureAwait(false);
                hashed++;
            }

            if (!result.TryAdd(item.Id, new LocalItem(item.Id, length, item.LastModifiedUtc, item.Fingerprint, hash)))
            {
                throw new SyncIntegrityException($"The data source returned the item '{item.Id}' twice.");
            }
        }

        LogDetected(logger, result.Count, hashed);
        return new LocalDetection(result, hashed);
    }

    private static string? KnownHash(SyncItem item, IReadOnlyDictionary<string, ItemSyncState> states)
    {
        if (ContentHash.IsValid(item.ContentHash)) return item.ContentHash;
        return item.Fingerprint is not null
            && states.TryGetValue(item.Id, out ItemSyncState? state)
            && state.LocalFingerprint == item.Fingerprint
            && ContentHash.IsValid(state.LocalHash)
                ? state.LocalHash
                : null;
    }

    private static async Task<(string Hash, long Length)> HashAsync(LocalReader reader, string itemId, CancellationToken cancellationToken)
    {
        Stream stream = await reader(itemId, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await ContentHash.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 1010, Level = LogLevel.Debug, Message = "Local scan: {Count} items, {Hashed} hashed.")]
    private static partial void LogDetected(ILogger logger, int count, int hashed);
}

/// <summary>Result of local detection.</summary>
internal sealed record LocalDetection(IReadOnlyDictionary<string, LocalItem> Items, int HashedCount);
