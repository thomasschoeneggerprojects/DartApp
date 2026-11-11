namespace Sta.DataSync;

/// <summary>One version of a conflicting item.</summary>
/// <param name="ContentHash">SHA-256 hash, or <see langword="null"/> when this side deleted the item.</param>
/// <param name="Length">Content length.</param>
/// <param name="ModifiedUtc">Modification time if known (local clock or the writing device's clock).</param>
/// <param name="DeviceId">Device that wrote the version, if known.</param>
public sealed record SyncConflictVersion(string? ContentHash, long Length, DateTimeOffset? ModifiedUtc, string? DeviceId)
{
    /// <summary><see langword="true"/> when this side deleted the item.</summary>
    public bool IsDeleted => ContentHash is null;
}

/// <summary>An item changed independently on both sides since the last common state.</summary>
/// <param name="ScopeId">The scope.</param>
/// <param name="ItemId">The item.</param>
/// <param name="Local">Local version.</param>
/// <param name="Remote">Remote version.</param>
/// <param name="BaseContentHash">Hash of the last common version, or <see langword="null"/> if there was none.</param>
/// <param name="DetectedUtc">When the conflict was detected.</param>
public sealed record SyncConflict(
    string ScopeId,
    string ItemId,
    SyncConflictVersion Local,
    SyncConflictVersion Remote,
    string? BaseContentHash,
    DateTimeOffset DetectedUtc);

/// <summary>Decision for a conflict.</summary>
/// <param name="Kind">The decision.</param>
/// <param name="ConflictCopyId">For <see cref="ConflictResolutionKind.KeepBoth"/>: id of the copy; generated when <see langword="null"/>.</param>
public sealed record ConflictResolution(ConflictResolutionKind Kind, string? ConflictCopyId = null)
{
    /// <summary>Upload the local version.</summary>
    public static ConflictResolution KeepLocal { get; } = new(ConflictResolutionKind.KeepLocal);

    /// <summary>Apply the remote version.</summary>
    public static ConflictResolution KeepRemote { get; } = new(ConflictResolutionKind.KeepRemote);

    /// <summary>Keep both versions.</summary>
    public static ConflictResolution KeepBoth { get; } = new(ConflictResolutionKind.KeepBoth);

    /// <summary>Leave the conflict for later.</summary>
    public static ConflictResolution Defer { get; } = new(ConflictResolutionKind.Defer);
}
