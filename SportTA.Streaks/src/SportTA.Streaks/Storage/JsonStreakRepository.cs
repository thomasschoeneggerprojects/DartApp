using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SportTA.Streaks.Storage.Json;

namespace SportTA.Streaks.Storage;

/// <summary>
/// Default <see cref="IStreakRepository"/>: one versioned JSON document per player, written atomically through an
/// <see cref="IStreakStorageProvider"/> (by default the host's directory via <see cref="FileStreakStorageProvider"/>).
/// </summary>
/// <remarks>
/// <para>Every read-modify-write runs under the provider's exclusive lock and checks the version, so concurrent writers
/// in the same or different processes cannot lose updates.</para>
/// <para>Corruption: if the current data is unreadable but the previous generation (backup) is valid, the corrupted data is
/// quarantined and the backup is restored. Otherwise <see cref="StreakDataCorruptedException"/> is thrown and nothing is
/// modified, unless <see cref="CorruptDataHandling.QuarantineAndReset"/> is configured.</para>
/// </remarks>
public sealed class JsonStreakRepository : IStreakRepository
{
    private const string KeyPrefix = "streaks-";
    private readonly IStreakStorageProvider _storage;
    private readonly CorruptDataHandling _corruptDataHandling;
    private readonly ILogger _logger;

    /// <summary>Creates a repository.</summary>
    public JsonStreakRepository(
        IStreakStorageProvider storage,
        CorruptDataHandling corruptDataHandling = CorruptDataHandling.Throw,
        ILogger<JsonStreakRepository>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!Enum.IsDefined(corruptDataHandling))
        {
            throw new ArgumentOutOfRangeException(nameof(corruptDataHandling));
        }

        _storage = storage;
        _corruptDataHandling = corruptDataHandling;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the storage key used for a player (the file name without extension for file storage).</summary>
    public static string GetStorageKey(Guid playerId) => $"{KeyPrefix}{playerId:N}";

    /// <inheritdoc />
    public async Task<Streak?> GetAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        string key = GetStorageKey(playerId);

        IAsyncDisposable handle = await _storage.AcquireLockAsync(key, cancellationToken).ConfigureAwait(false);
        await using (handle.ConfigureAwait(false))
        {
            List<Streak> streaks = await LoadAsync(key, playerId, cancellationToken).ConfigureAwait(false);
            return streaks.Find(s => string.Equals(s.DefinitionId, definitionId, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Streak>> GetAllAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        string key = GetStorageKey(playerId);

        IAsyncDisposable handle = await _storage.AcquireLockAsync(key, cancellationToken).ConfigureAwait(false);
        await using (handle.ConfigureAwait(false))
        {
            return await LoadAsync(key, playerId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(Streak streak, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streak);
        ValidatePlayer(streak.PlayerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streak.DefinitionId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (streak.Version <= expectedVersion)
        {
            throw new ArgumentException("The streak version must be greater than the expected version.", nameof(streak));
        }

        string key = GetStorageKey(streak.PlayerId);
        IAsyncDisposable handle = await _storage.AcquireLockAsync(key, cancellationToken).ConfigureAwait(false);
        await using (handle.ConfigureAwait(false))
        {
            List<Streak> streaks = await LoadAsync(key, streak.PlayerId, cancellationToken).ConfigureAwait(false);
            int index = streaks.FindIndex(s => string.Equals(s.DefinitionId, streak.DefinitionId, StringComparison.Ordinal));
            long storedVersion = index >= 0 ? streaks[index].Version : 0;
            if (storedVersion != expectedVersion)
            {
                throw new StreakConcurrencyException(
                    $"Streak '{streak.DefinitionId}' was modified concurrently (expected version {expectedVersion}, found {storedVersion}).");
            }

            if (index >= 0)
            {
                streaks[index] = streak;
            }
            else
            {
                streaks.Add(streak);
            }

            byte[] data = StreakJsonFormat.Serialize(streak.PlayerId, streaks);
            await _storage.WriteAsync(key, data, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid playerId, string definitionId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        string key = GetStorageKey(playerId);

        IAsyncDisposable handle = await _storage.AcquireLockAsync(key, cancellationToken).ConfigureAwait(false);
        await using (handle.ConfigureAwait(false))
        {
            List<Streak> streaks = await LoadAsync(key, playerId, cancellationToken).ConfigureAwait(false);
            int removed = streaks.RemoveAll(s => string.Equals(s.DefinitionId, definitionId, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            await _storage.WriteAsync(key, StreakJsonFormat.Serialize(playerId, streaks), cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private async Task<List<Streak>> LoadAsync(string key, Guid playerId, CancellationToken cancellationToken)
    {
        byte[]? data = await _storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            return [];
        }

        try
        {
            return StreakJsonFormat.Deserialize(data, playerId);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return await RecoverAsync(key, playerId, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<Streak>> RecoverAsync(string key, Guid playerId, Exception error, CancellationToken cancellationToken)
    {
        byte[]? backup = await _storage.ReadBackupAsync(key, cancellationToken).ConfigureAwait(false);
        if (backup is not null)
        {
            try
            {
                List<Streak> recovered = StreakJsonFormat.Deserialize(backup, playerId);
                string? quarantined = await _storage.QuarantineAsync(key, cancellationToken).ConfigureAwait(false);
                await _storage.WriteAsync(key, backup, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    error,
                    "Streak data '{Key}' was corrupted and has been restored from its backup. The corrupted data was kept at '{Location}'.",
                    key,
                    quarantined);
                return recovered;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                _logger.LogWarning(ex, "The backup of streak data '{Key}' is not usable either.", key);
            }
        }

        if (_corruptDataHandling == CorruptDataHandling.QuarantineAndReset)
        {
            string? quarantined = await _storage.QuarantineAsync(key, cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                error,
                "Streak data '{Key}' was corrupted and has been reset. The corrupted data was kept at '{Location}'.",
                key,
                quarantined);
            return [];
        }

        throw new StreakDataCorruptedException(key, error);
    }

    private static void ValidatePlayer(Guid playerId)
    {
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("The player id must not be empty.", nameof(playerId));
        }
    }
}
