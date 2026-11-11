namespace Sta.DataSync;

/// <summary>A local item of a data source.</summary>
/// <param name="Id">Stable logical id, a relative path with '/' separators (see <see cref="SyncPath"/>).</param>
/// <param name="Length">Content length in bytes.</param>
/// <param name="LastModifiedUtc">Modification time, if the source knows it. Supporting metadata only.</param>
/// <param name="Fingerprint">
/// Cheap change indicator (e.g. length and modification ticks). When it equals the fingerprint of the last hashed
/// version, the engine reuses the stored hash instead of reading the content again. <see langword="null"/> forces hashing.
/// </param>
/// <param name="ContentHash">The SHA-256 hash (<c>sha256:&lt;hex&gt;</c>) if the source already knows it.</param>
public sealed record SyncItem(
    string Id,
    long Length,
    DateTimeOffset? LastModifiedUtc = null,
    string? Fingerprint = null,
    string? ContentHash = null);

/// <summary>An item stored at the destination.</summary>
/// <param name="Path">Logical path relative to the provider's namespace root.</param>
/// <param name="ProviderItemId">The provider's own id of the item.</param>
/// <param name="Version">Opaque version used for conditional writes.</param>
/// <param name="Length">Content length in bytes.</param>
/// <param name="ContentHash">SHA-256 hash (<c>sha256:&lt;hex&gt;</c>) when the provider reports it.</param>
/// <param name="CreatedUtc">Server creation time, when available.</param>
/// <param name="ModifiedUtc">Server modification time, when available.</param>
public sealed record RemoteSyncItem(
    string Path,
    string ProviderItemId,
    string Version,
    long Length,
    string? ContentHash = null,
    DateTimeOffset? CreatedUtc = null,
    DateTimeOffset? ModifiedUtc = null);

/// <summary>Describes content to store at the destination.</summary>
/// <param name="Path">Logical path relative to the provider's namespace root.</param>
/// <param name="ContentLength">Exact length of the content, if known.</param>
/// <param name="ContentHash">SHA-256 hash of the content (<c>sha256:&lt;hex&gt;</c>), if known.</param>
/// <param name="ExpectedVersion">
/// Only for providers with <see cref="SyncProviderCapabilities.ConditionalWrites"/>: replace only if the stored version
/// still matches; <see cref="RemoteUpload.CreateOnly"/> as the value means "must not exist yet".
/// </param>
public sealed record RemoteUpload(
    string Path,
    long? ContentLength = null,
    string? ContentHash = null,
    string? ExpectedVersion = null)
{
    /// <summary>Value for <see cref="ExpectedVersion"/> that requires the item not to exist.</summary>
    public const string CreateOnly = "<create-only>";
}

/// <summary>A change to apply to a data source.</summary>
public sealed class SyncChange
{
    /// <summary>Creates a change.</summary>
    public SyncChange(SyncChangeKind kind, string itemId)
    {
        SyncPath.Validate(itemId);
        Kind = kind;
        ItemId = itemId;
    }

    /// <summary>Write or delete.</summary>
    public SyncChangeKind Kind { get; }

    /// <summary>The item to change.</summary>
    public string ItemId { get; }

    /// <summary>
    /// Content for <see cref="SyncChangeKind.Write"/>. The stream verifies <see cref="ExpectedHash"/> and throws
    /// <see cref="SyncIntegrityException"/> at its end on a mismatch, so sources must read it to the end before they
    /// replace the existing item.
    /// </summary>
    public Stream? Content { get; init; }

    /// <summary>Expected content length for writes.</summary>
    public long? ExpectedLength { get; init; }

    /// <summary>Expected SHA-256 of the new content (<c>sha256:&lt;hex&gt;</c>).</summary>
    public string? ExpectedHash { get; init; }

    /// <summary>
    /// The local version the engine planned against: the item's fingerprint at detection time, or <see langword="null"/>
    /// when the item did not exist. Sources must refuse the change with <see cref="SyncConcurrencyException"/> when the
    /// current version differs, so a local edit made during synchronization is never overwritten.
    /// </summary>
    public string? ExpectedCurrentFingerprint { get; init; }

    /// <summary><see langword="true"/> when the item must not exist yet (<see cref="ExpectedCurrentFingerprint"/> is then ignored).</summary>
    public bool ExpectNotExists { get; init; }
}
