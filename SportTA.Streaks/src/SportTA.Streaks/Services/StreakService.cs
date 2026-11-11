using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SportTA.Streaks.Evaluation;
using SportTA.Streaks.Internal;

namespace SportTA.Streaks;

/// <summary>
/// Default <see cref="IStreakService"/>. Lightweight and stateless apart from configuration; safe to create per
/// request or once per application.
/// </summary>
/// <remarks>
/// Concurrency: operations on the same player and streak are serialized in-process. Across service instances and
/// processes, correctness relies on the repository's optimistic concurrency: a conflicting write is retried against
/// fresh data up to <see cref="StreakOptions.MaxConcurrencyRetries"/> times. Events are published only after a
/// successful save.
/// </remarks>
public sealed class StreakService : IStreakService
{
    private readonly IStreakRepository _repository;
    private readonly StreakRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly IStreakEventHandler[] _eventHandlers;
    private readonly ILogger _logger;
    private readonly KeyedAsyncLock _locks;
    private readonly TimeZoneInfo _defaultTimeZone;
    private readonly Func<Guid, TimeZoneInfo>? _timeZoneResolver;
    private readonly int _activityHistorySize;
    private readonly TimeSpan _maxFutureSkew;
    private readonly int _maxConcurrencyRetries;

    /// <summary>Creates a service (manual construction without dependency injection).</summary>
    /// <param name="repository">Persistence implementation.</param>
    /// <param name="registry">Registered streak definitions.</param>
    /// <param name="options">Options; defaults are used when <see langword="null"/>.</param>
    /// <param name="timeProvider">Clock; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    /// <param name="eventHandlers">Optional event handlers.</param>
    /// <param name="logger">Optional logger.</param>
    public StreakService(
        IStreakRepository repository,
        StreakRegistry registry,
        StreakOptions? options = null,
        TimeProvider? timeProvider = null,
        IEnumerable<IStreakEventHandler>? eventHandlers = null,
        ILogger<StreakService>? logger = null)
        : this(repository, registry, options, timeProvider, eventHandlers, logger, new KeyedAsyncLock())
    {
    }

    internal StreakService(
        IStreakRepository repository,
        StreakRegistry registry,
        StreakOptions? options,
        TimeProvider? timeProvider,
        IEnumerable<IStreakEventHandler>? eventHandlers,
        ILogger<StreakService>? logger,
        KeyedAsyncLock locks)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(locks);
        options ??= new StreakOptions();
        options.Validate();

        _repository = repository;
        _registry = registry;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _eventHandlers = eventHandlers?.ToArray() ?? [];
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _locks = locks;
        _defaultTimeZone = options.DefaultTimeZone;
        _timeZoneResolver = options.PlayerTimeZoneResolver;
        _activityHistorySize = options.ProcessedActivityHistorySize;
        _maxFutureSkew = options.MaxFutureTimestampSkew;
        _maxConcurrencyRetries = options.MaxConcurrencyRetries;
    }

    /// <inheritdoc />
    public Task<StreakResult> RecordActivityAsync(Guid playerId, string streakId, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
        => RecordActivityAsync(playerId, streakId, StreakActivity.Create(timestamp), cancellationToken);

    /// <inheritdoc />
    public Task<StreakResult> RecordActivityAsync(Guid playerId, string streakId, StreakActivity activity, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        StreakDefinition definition = GetDefinition(streakId);
        StreakActivity normalized = NormalizeActivity(activity);
        return MutateAsync(playerId, definition, m => m.RecordActivity(normalized, _maxFutureSkew), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StreakResult>> RecordActivityForAllAsync(Guid playerId, StreakActivity activity, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        StreakActivity normalized = NormalizeActivity(activity);
        var results = new List<StreakResult>();
        foreach (StreakDefinition definition in _registry.Definitions)
        {
            results.Add(await MutateAsync(playerId, definition, m => m.RecordActivity(normalized, _maxFutureSkew), cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<StreakProgress> GetProgressAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        StreakDefinition definition = GetDefinition(streakId);
        cancellationToken.ThrowIfCancellationRequested();
        Streak? stored = await _repository.GetAsync(playerId, definition.Id, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        return Project(stored, playerId, definition, now);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StreakProgress>> GetAllAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Streak> stored = await _repository.GetAllAsync(playerId, cancellationToken).ConfigureAwait(false);
        var byDefinition = new Dictionary<string, Streak>(StringComparer.Ordinal);
        foreach (Streak streak in stored)
        {
            byDefinition.TryAdd(streak.DefinitionId, streak);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        return _registry.Definitions
            .Select(definition => Project(byDefinition.GetValueOrDefault(definition.Id), playerId, definition, now))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<StreakStatistics> GetStatisticsAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        StreakDefinition definition = GetDefinition(streakId);
        cancellationToken.ThrowIfCancellationRequested();
        Streak? stored = await _repository.GetAsync(playerId, definition.Id, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        Streak state = stored ?? Streak.CreateNew(playerId, definition.Id, now);
        var projection = new StreakMutation(state, definition, ResolveTimeZone(playerId), now, _activityHistorySize);
        projection.SettleUntilNow();
        return StreakSnapshots.CreateStatistics(projection.State);
    }

    /// <inheritdoc />
    public Task<StreakResult> EvaluateAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        StreakDefinition definition = GetDefinition(streakId);
        return MutateAsync(playerId, definition, m => m.Evaluate(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StreakResult>> EvaluateAllAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        var results = new List<StreakResult>();
        foreach (StreakDefinition definition in _registry.Definitions)
        {
            results.Add(await MutateAsync(playerId, definition, m => m.Evaluate(), cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <inheritdoc />
    public Task<StreakResult> AddFreezesAsync(Guid playerId, string streakId, int count, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        StreakDefinition definition = GetDefinition(streakId);
        return MutateAsync(playerId, definition, m => m.AddFreezes(count), cancellationToken);
    }

    /// <inheritdoc />
    public Task<StreakResult> RestoreAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        StreakDefinition definition = GetDefinition(streakId);
        return MutateAsync(playerId, definition, m => m.Restore(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<StreakResult> ResetAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        StreakDefinition definition = GetDefinition(streakId);
        return MutateAsync(playerId, definition, m => m.Reset(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<StreakResult> SetMetadataAsync(Guid playerId, string streakId, string key, string? value, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        StreakDefinition definition = GetDefinition(streakId);
        return MutateAsync(playerId, definition, m => m.SetMetadata(key, value), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid playerId, string streakId, CancellationToken cancellationToken = default)
    {
        ValidatePlayer(playerId);
        StreakDefinition definition = GetDefinition(streakId);
        cancellationToken.ThrowIfCancellationRequested();
        using (await _locks.AcquireAsync(LockKey(playerId, definition.Id), cancellationToken).ConfigureAwait(false))
        {
            return await _repository.DeleteAsync(playerId, definition.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<StreakResult> MutateAsync(Guid playerId, StreakDefinition definition, Action<StreakMutation> apply, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeZoneInfo timeZone = ResolveTimeZone(playerId);
        StreakMutation mutation;
        DateTimeOffset now;

        using (await _locks.AcquireAsync(LockKey(playerId, definition.Id), cancellationToken).ConfigureAwait(false))
        {
            for (int attempt = 0; ; attempt++)
            {
                now = _timeProvider.GetUtcNow();
                Streak? existing = await _repository.GetAsync(playerId, definition.Id, cancellationToken).ConfigureAwait(false);
                Streak state = existing ?? Streak.CreateNew(playerId, definition.Id, now);

                mutation = new StreakMutation(state, definition, timeZone, now, _activityHistorySize);
                apply(mutation);

                if (!mutation.Changed || (existing is null && !IsWorthStoring(mutation.State)))
                {
                    // Nothing to persist (e.g. duplicate, ignored or rejected activity); report the stored state.
                    return CreateResult(mutation, state, definition, timeZone, now, persisted: existing is not null, stateChanged: false, events: []);
                }

                Streak next = mutation.State with { Version = state.Version + 1, UpdatedAt = now };
                try
                {
                    await _repository.SaveAsync(next, state.Version, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (StreakConcurrencyException) when (attempt < _maxConcurrencyRetries)
                {
                    _logger.LogDebug("Concurrent update of streak {DefinitionId} for player {PlayerId}; retrying.", definition.Id, playerId);
                }
            }
        }

        // The change is durable from here on; never throw because of notifications.
        await PublishAsync(mutation.Events, cancellationToken).ConfigureAwait(false);
        return CreateResult(mutation, mutation.State, definition, timeZone, now, persisted: true, stateChanged: true, events: mutation.Events);
    }

    private StreakResult CreateResult(
        StreakMutation mutation,
        Streak state,
        StreakDefinition definition,
        TimeZoneInfo timeZone,
        DateTimeOffset now,
        bool persisted,
        bool stateChanged,
        IReadOnlyList<StreakEvent> events)
    {
        var projection = new StreakMutation(state, definition, timeZone, now, _activityHistorySize);
        projection.SettleUntilNow();
        return new StreakResult
        {
            Code = mutation.Code,
            Progress = StreakSnapshots.CreateProgress(projection.State, definition, timeZone, now, persisted),
            Events = events,
            StateChanged = stateChanged,
        };
    }

    private StreakProgress Project(Streak? stored, Guid playerId, StreakDefinition definition, DateTimeOffset now)
    {
        TimeZoneInfo timeZone = ResolveTimeZone(playerId);
        Streak state = stored ?? Streak.CreateNew(playerId, definition.Id, now);
        var projection = new StreakMutation(state, definition, timeZone, now, _activityHistorySize);
        projection.SettleUntilNow();
        return StreakSnapshots.CreateProgress(projection.State, definition, timeZone, now, stored is not null);
    }

    private async Task PublishAsync(IReadOnlyList<StreakEvent> events, CancellationToken cancellationToken)
    {
        foreach (StreakEvent streakEvent in events)
        {
            foreach (IStreakEventHandler handler in _eventHandlers)
            {
                try
                {
                    await handler.HandleAsync(streakEvent, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Streak event handler {Handler} failed for {EventType} of streak {DefinitionId}. The streak state is unaffected.",
                        handler.GetType().Name,
                        streakEvent.Type,
                        streakEvent.DefinitionId);
                }
            }
        }
    }

    private static bool IsWorthStoring(Streak state)
        => state.TotalQualifyingActivities > 0 || state.FreezesAvailable > 0 || state.Metadata.Count > 0;

    private TimeZoneInfo ResolveTimeZone(Guid playerId)
        => _timeZoneResolver?.Invoke(playerId) ?? _defaultTimeZone;

    private StreakDefinition GetDefinition(string streakId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streakId);
        return _registry.Get(streakId);
    }

    private static StreakActivity NormalizeActivity(StreakActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        activity.Validate();
        return activity with { Timestamp = activity.Timestamp.ToUniversalTime() };
    }

    private static void ValidatePlayer(Guid playerId)
    {
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("The player id must not be empty.", nameof(playerId));
        }
    }

    private static string LockKey(Guid playerId, string definitionId) => $"{playerId:N}:{definitionId}";
}
