namespace SportTA.Streaks;

/// <summary>
/// Low-level byte storage used by <see cref="Storage.JsonStreakRepository"/>. Implement it to keep the JSON format
/// but store the bytes elsewhere (encrypted storage, a game save container, a cloud blob, ...).
/// </summary>
public interface IStreakStorageProvider
{
    /// <summary>Acquires an exclusive lock for <paramref name="key"/>. Disposing the handle releases it.</summary>
    Task<IAsyncDisposable> AcquireLockAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Reads the current data, or <see langword="null"/> when none exists.</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Reads the previous generation kept by <see cref="WriteAsync"/>, or <see langword="null"/>.</summary>
    Task<byte[]?> ReadBackupAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces the data; the previous data becomes the backup. Must never leave partial data.</summary>
    Task WriteAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Moves unreadable data aside (never deletes it). Returns its new location, or <see langword="null"/> if nothing existed.</summary>
    Task<string?> QuarantineAsync(string key, CancellationToken cancellationToken = default);
}
