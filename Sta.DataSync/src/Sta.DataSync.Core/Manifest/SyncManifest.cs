using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sta.DataSync.Manifest;

/// <summary>
/// Versioned description of a scope's remote state. Unknown properties are preserved on round trips, so older clients
/// never drop data written by newer ones; a higher major <see cref="SchemaVersion"/> is refused instead of overwritten.
/// </summary>
internal sealed class SyncManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public long Revision { get; set; }

    /// <summary>Remote path of the manifest this one was built on.</summary>
    public string? Parent { get; set; }

    /// <summary>Session that wrote this revision (used by crash recovery).</summary>
    public string CommitId { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public List<SyncManifestEntry> Items { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    public Dictionary<string, SyncManifestEntry> ToDictionary()
    {
        var map = new Dictionary<string, SyncManifestEntry>(Items.Count, StringComparer.Ordinal);
        foreach (SyncManifestEntry entry in Items)
        {
            if (!SyncPath.IsValid(entry.Id)) throw new SyncIntegrityException($"The remote manifest contains an invalid item id '{entry.Id}'.");
            if (!map.TryAdd(entry.Id, entry)) throw new SyncIntegrityException($"The remote manifest lists '{entry.Id}' twice.");
            if (!entry.Deleted && (!ContentHash.IsValid(entry.Hash) || entry.Size < 0 || string.IsNullOrEmpty(entry.Object)))
            {
                throw new SyncIntegrityException($"The remote manifest entry '{entry.Id}' is incomplete.");
            }
        }

        return map;
    }
}

/// <summary>One item (or tombstone) of a manifest.</summary>
internal sealed class SyncManifestEntry
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Increases with every change of the item.</summary>
    public long Version { get; set; }

    public string? Hash { get; set; }

    public long Size { get; set; }

    public bool Deleted { get; set; }

    public DateTimeOffset? ModifiedUtc { get; set; }

    public string? ModifiedBy { get; set; }

    /// <summary>Remote object name holding the content.</summary>
    public string? Object { get; set; }

    /// <summary>Content transform applied to the object, if any.</summary>
    public string? Transform { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    public SyncManifestEntry Clone() => (SyncManifestEntry)MemberwiseClone();
}

/// <summary>JSON (de)serialization of manifests with defensive checks.</summary>
internal static class ManifestSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<SyncManifest> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        SyncManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<SyncManifest>(stream, Options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SyncIntegrityException("The remote manifest is not valid JSON.", ex);
        }

        if (manifest is null) throw new SyncIntegrityException("The remote manifest is empty.");
        if (manifest.SchemaVersion > SyncManifest.CurrentSchemaVersion)
        {
            throw new SyncIntegrityException(
                $"The remote manifest uses schema {manifest.SchemaVersion}; update the application to synchronize this scope.");
        }

        manifest.Items ??= [];
        return manifest;
    }

    public static MemoryStream Write(SyncManifest manifest)
    {
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, manifest, Options);
        stream.Position = 0;
        return stream;
    }
}
