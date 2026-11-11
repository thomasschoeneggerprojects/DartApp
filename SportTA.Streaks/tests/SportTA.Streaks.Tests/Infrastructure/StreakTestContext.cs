namespace SportTA.Streaks.Tests.Infrastructure;

/// <summary>Wires a <see cref="StreakService"/> with a fake clock, an in-memory repository and a recording handler.</summary>
public sealed class StreakTestContext
{
    public static readonly Guid DefaultPlayerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public StreakTestContext(
        DateTimeOffset start,
        IEnumerable<StreakDefinition> definitions,
        Action<StreakOptions>? configure = null,
        IStreakRepository? repository = null,
        IEnumerable<IStreakEventHandler>? extraHandlers = null)
    {
        Time = new FakeTimeProvider(start);
        Repository = repository ?? new InMemoryStreakRepository();
        Registry = new StreakRegistry(definitions);
        Options = new StreakOptions();
        configure?.Invoke(Options);

        var handlers = new List<IStreakEventHandler>(extraHandlers ?? []);
        handlers.Add(Handler);
        Service = new StreakService(Repository, Registry, Options, Time, handlers);
    }

    public FakeTimeProvider Time { get; }

    public IStreakRepository Repository { get; }

    public StreakRegistry Registry { get; }

    public StreakOptions Options { get; }

    public RecordingEventHandler Handler { get; } = new();

    public StreakService Service { get; }

    public Guid PlayerId { get; init; } = DefaultPlayerId;

    public static DateTimeOffset Utc(int year, int month, int day, int hour = 12, int minute = 0)
        => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>Moves the clock to <paramref name="when"/> and records an activity happening at that instant.</summary>
    public Task<StreakResult> RecordAtAsync(
        DateTimeOffset when,
        string streakId,
        string? activityId = null,
        ActivityOutcome outcome = ActivityOutcome.Success,
        string? category = null,
        double? value = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        Guid? playerId = null)
    {
        Time.SetUtcNow(when);
        return Service.RecordActivityAsync(playerId ?? PlayerId, streakId, new StreakActivity
        {
            Id = activityId ?? Guid.NewGuid().ToString("N"),
            Timestamp = when,
            Outcome = outcome,
            Category = category,
            Value = value,
            Metadata = metadata,
        });
    }

    public Task<StreakResult> EvaluateAtAsync(DateTimeOffset when, string streakId)
    {
        Time.SetUtcNow(when);
        return Service.EvaluateAsync(PlayerId, streakId);
    }

    public Task<StreakProgress> ProgressAtAsync(DateTimeOffset when, string streakId)
    {
        Time.SetUtcNow(when);
        return Service.GetProgressAsync(PlayerId, streakId);
    }
}
