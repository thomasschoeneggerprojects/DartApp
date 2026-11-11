using System.Collections.Concurrent;
using StreakCelebration.Models;
using StreakCelebration.Services;

namespace StreakCelebration.Tests;

/// <summary>Host whose celebrations finish only when the test says so; records order and concurrency.</summary>
internal sealed class FakeHost : IStreakCelebrationHost
{
    private readonly object _gate = new();
    private readonly List<Call> _calls = [];
    private bool _available;
    private int _running;

    public FakeHost(bool available = true, string name = "host")
    {
        _available = available;
        Name = name;
    }

    public string Name { get; }

    /// <summary>When set, every celebration completes immediately with this result.</summary>
    public StreakCelebrationResult? AutoComplete { get; set; }

    /// <summary>When set, PlayAsync throws this.</summary>
    public Exception? Failure { get; set; }

    public int MaxConcurrent { get; private set; }

    public bool IsAvailable
    {
        get { lock (_gate) return _available; }
    }

    public event EventHandler? AvailabilityChanged;

    public IReadOnlyList<Call> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    public void SetAvailable(bool available)
    {
        lock (_gate) _available = available;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<StreakCelebrationResult> PlayAsync(CelebrationPlayback playback, CancellationToken cancellationToken)
    {
        if (Failure is not null) return Task.FromException<StreakCelebrationResult>(Failure);

        var call = new Call(playback, cancellationToken);
        lock (_gate)
        {
            _calls.Add(call);
            _running++;
            MaxConcurrent = Math.Max(MaxConcurrent, _running);
        }

        if (AutoComplete is { } result) call.Finish(result);
        return Track(call);
    }

    private async Task<StreakCelebrationResult> Track(Call call)
    {
        try
        {
            return await call.Completion.Task;
        }
        finally
        {
            lock (_gate) _running--;
        }
    }

    internal sealed class Call
    {
        public Call(CelebrationPlayback playback, CancellationToken token)
        {
            Playback = playback;
            Token = token;
            // Like the real overlay: cancellation ends the celebration with OperationCanceledException.
            token.Register(() => Completion.TrySetCanceled(token));
        }

        public CelebrationPlayback Playback { get; }

        public CancellationToken Token { get; }

        public TaskCompletionSource<StreakCelebrationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Title => Playback.Data.Title;

        public void Finish(StreakCelebrationResult result = StreakCelebrationResult.Completed) => Completion.TrySetResult(result);
    }
}

internal sealed class RecordingSoundPlayer : ICelebrationSoundPlayer
{
    public ConcurrentQueue<string> Played { get; } = new();

    public bool Fail { get; set; }

    public Task PlayAsync(string soundFile, CancellationToken cancellationToken)
    {
        if (Fail) throw new IOException("speaker missing");
        Played.Enqueue(soundFile);
        return Task.CompletedTask;
    }
}

internal sealed class FixedReducedMotion(bool enabled) : IReducedMotionProvider
{
    public bool IsReducedMotionEnabled => enabled;
}

internal sealed class CountingDispatcher : ICelebrationDispatcher
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        Interlocked.Increment(ref _calls);
        return Task.Run(action);
    }
}

internal static class Wait
{
    /// <summary>Waits until <paramref name="condition"/> holds (the queue runs on the thread pool).</summary>
    public static async Task Until(Func<bool> condition, string because = "condition")
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {because}.");
            await Task.Delay(5);
        }
    }

    /// <summary>Gives the background queue a moment, to prove that something does NOT happen.</summary>
    public static Task Briefly() => Task.Delay(100);
}

internal static class Celebrations
{
    public static StreakCelebrationData Data(string title, int count = 5, CelebrationLevel level = CelebrationLevel.Normal)
        => new() { Title = title, Count = count, Level = level };
}
