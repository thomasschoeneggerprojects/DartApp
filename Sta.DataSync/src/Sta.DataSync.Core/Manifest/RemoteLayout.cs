using System.Globalization;

namespace Sta.DataSync.Manifest;

/// <summary>
/// Remote paths of a scope:
/// <code>
/// {root}/{application}/{scope}/manifest.json                         (providers with conditional writes)
/// {root}/{application}/{scope}/manifests/{revision}-{device}-{nonce}.json  (revision log, all other providers)
/// {root}/{application}/{scope}/objects/{sha256 hex}[.{transform}]       (immutable, content-addressed)
/// </code>
/// </summary>
internal sealed class RemoteLayout
{
    private const string ManifestFolder = "manifests";
    private const string ObjectFolder = "objects";

    public RemoteLayout(string rootFolder, string applicationId, string scopeId)
    {
        ScopePrefix = SyncPath.Combine(rootFolder, applicationId, scopeId);
        ManifestPath = ScopePrefix + "/manifest.json";
        RevisionPrefix = ScopePrefix + "/" + ManifestFolder + "/";
        ObjectPrefix = ScopePrefix + "/" + ObjectFolder + "/";
    }

    /// <summary>Prefix of everything in the scope (without trailing '/').</summary>
    public string ScopePrefix { get; }

    public string ListPrefix => ScopePrefix + "/";

    public string ManifestPath { get; }

    public string RevisionPrefix { get; }

    public string ObjectPrefix { get; }

    public string RevisionPath(long revision, string deviceId, string nonce)
        => RevisionPrefix + revision.ToString("D12", CultureInfo.InvariantCulture) + "-" + deviceId + "-" + nonce + ".json";

    public string ObjectPath(string objectName) => ObjectPrefix + objectName;

    public static string ObjectName(string contentHash, string? transformId)
        => transformId is null ? ContentHash.HexOf(contentHash) : ContentHash.HexOf(contentHash) + "." + transformId;

    /// <summary>Revision number of a revision-log manifest path, or <see langword="null"/> for other paths.</summary>
    public long? RevisionOf(string path)
    {
        if (!path.StartsWith(RevisionPrefix, StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal)) return null;
        ReadOnlySpan<char> name = path.AsSpan(RevisionPrefix.Length);
        int dash = name.IndexOf('-');
        return dash > 0 && long.TryParse(name[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out long revision) ? revision : null;
    }
}
