using System.Collections.Concurrent;

namespace SportTA.Streaks.Tests.Infrastructure;

public sealed class RecordingEventHandler : IStreakEventHandler
{
    private readonly ConcurrentQueue<StreakEvent> _events = new();

    public IReadOnlyList<StreakEvent> Events => _events.ToArray();

    public void Clear() => _events.Clear();

    public Task HandleAsync(StreakEvent streakEvent, CancellationToken cancellationToken)
    {
        _events.Enqueue(streakEvent);
        return Task.CompletedTask;
    }
}

public sealed class ThrowingEventHandler : IStreakEventHandler
{
    public int Calls { get; private set; }

    public Task HandleAsync(StreakEvent streakEvent, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("Handler failure.");
    }
}
