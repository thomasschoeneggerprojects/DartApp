using System.Diagnostics;
using SportTA.Streaks.Internal;

namespace SportTA.Streaks.Storage;

/// <summary>
/// Stores streak data as files in a directory supplied by the hosting application. The library never chooses a
/// location on its own: no current directory, base directory or special folder is ever used.
/// </summary>
/// <remarks>
/// <para>Files per key: <c>{key}.json</c> (data), <c>{key}.json.bak</c> (previous generation), <c>{key}.lock</c>
/// (cross-process lock) and, after corruption, <c>{key}.json.corrupt-*</c>.</para>
/// <para>Writes go to a temporary file in the same directory which then atomically replaces the data file, so readers
/// never observe partial writes. Locking combines an in-process async lock with an exclusive lock file
/// (<see cref="FileShare.None"/>), which also serializes other processes and other provider instances using the same directory.</para>
/// </remarks>
public sealed class FileStreakStorageProvider : IStreakStorageProvider
{
    private const int MaxKeyLength = 128;
    private const int ReplaceAttempts = 5;
    private readonly KeyedAsyncLock _locks = new();
    private readonly TimeSpan _lockTimeout;

    /// <summary>Creates a provider for the application's data directory.</summary>
    /// <param name="storageDirectory">Absolute path of the directory the application already uses for its data. Created on first write if missing.</param>
    /// <param name="lockTimeout">How long to wait for a lock held by another process. Default 10 seconds.</param>
    public FileStreakStorageProvider(string storageDirectory, TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        if (!Path.IsPathFullyQualified(storageDirectory))
        {
            throw new ArgumentException("The storage directory must be an absolute path supplied by the hosting application.", nameof(storageDirectory));
        }

        if (lockTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout), "The lock timeout must be positive.");
        }

        StorageDirectory = Path.GetFullPath(storageDirectory);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>The directory supplied by the application.</summary>
    public string StorageDirectory { get; }

    /// <summary>Gets the data file path for a key.</summary>
    public string GetFilePath(string key)
    {
        ValidateKey(key);
        return Path.Combine(StorageDirectory, key + ".json");
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireLockAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        KeyedAsyncLock.Releaser releaser = await _locks.AcquireAsync(key, cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectoryExists();
            FileStream lockFile = await OpenLockFileAsync(Path.Combine(StorageDirectory, key + ".lock"), cancellationToken).ConfigureAwait(false);
            return new LockHandle(releaser, lockFile);
        }
        catch
        {
            releaser.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        => ReadFileAsync(GetFilePath(key), cancellationToken);

    /// <inheritdoc />
    public Task<byte[]?> ReadBackupAsync(string key, CancellationToken cancellationToken = default)
        => ReadFileAsync(GetFilePath(key) + ".bak", cancellationToken);

    /// <inheritdoc />
    public async Task WriteAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        string path = GetFilePath(key);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            EnsureDirectoryExists();
            var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Past this point the write is committed atomically; cancellation is no longer observed.
            await ReplaceAsync(tempPath, path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw new StreakStorageException($"Failed to write streak data to '{path}'.", ex);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<string?> QuarantineAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetFilePath(key);
        if (!File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        string target = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd'T'HHmmss}-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StreakStorageException($"Failed to quarantine corrupted streak data '{path}'.", ex);
        }

        return Task.FromResult<string?>(target);
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StreakStorageException($"Failed to read streak data from '{path}'.", ex);
        }
    }

    private static async Task ReplaceAsync(string tempPath, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, path + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path, overwrite: true);
                }

                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
            {
                // Transient sharing violations (virus scanners, indexers) on Windows.
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt)).ConfigureAwait(false);
            }
        }
    }

    private async Task<FileStream> OpenLockFileAsync(string path, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
            }
            catch (IOException ex)
            {
                if (stopwatch.Elapsed >= _lockTimeout)
                {
                    throw new StreakStorageException($"Timed out after {_lockTimeout} waiting for the streak storage lock '{path}'.", ex);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new StreakStorageException($"Access to the streak storage lock '{path}' was denied.", ex);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 200));
        }
    }

    private void EnsureDirectoryExists()
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StreakStorageException($"The streak storage directory '{StorageDirectory}' is not usable.", ex);
        }
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Length > MaxKeyLength || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("Storage keys may only contain ASCII letters, digits, '-' and '_' (max. 128 characters).", nameof(key));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray .tmp file never affects the data file.
        }
    }

    private sealed class LockHandle : IAsyncDisposable
    {
        private readonly KeyedAsyncLock.Releaser _releaser;
        private FileStream? _lockFile;

        public LockHandle(KeyedAsyncLock.Releaser releaser, FileStream lockFile)
        {
            _releaser = releaser;
            _lockFile = lockFile;
        }

        public async ValueTask DisposeAsync()
        {
            FileStream? lockFile = Interlocked.Exchange(ref _lockFile, null);
            if (lockFile is null)
            {
                return;
            }

            try
            {
                await lockFile.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _releaser.Dispose();
            }
        }
    }
}
