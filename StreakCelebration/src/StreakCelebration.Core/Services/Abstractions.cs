using StreakCelebration.Animations;
using StreakCelebration.Models;

namespace StreakCelebration.Services;

/// <summary>
/// Shows celebrations one after another above the app's content. Thread-safe; call from any thread.
/// </summary>
/// <remarks>
/// <para>Requests are queued (first in, first out) and never overlap. A request waits while no overlay host is
/// available, e.g. while the app is in the background, and expires after <see cref="StreakCelebrationOptions.QueuedLifetime"/>.</para>
/// <para>Cancelling the token removes a waiting request, or fades out a running celebration quickly; in both cases the
/// returned task is cancelled. Losing the host while a celebration runs (page unloaded, app backgrounded) ends it at once
/// with <see cref="StreakCelebrationResult.Interrupted"/>; the next request plays on the next available host.</para>
/// </remarks>
public interface IStreakCelebrationService
{
    /// <summary>Queues a celebration; the task completes when it ended or was removed.</summary>
    /// <exception cref="ArgumentException"><paramref name="data"/> is invalid.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<StreakCelebrationResult> ShowAsync(StreakCelebrationData data, CancellationToken cancellationToken = default);

    /// <summary><see langword="true"/> while a celebration is on screen.</summary>
    bool IsShowing { get; }

    /// <summary>Requests waiting to be shown.</summary>
    int PendingCount { get; }

    /// <summary>Fades out the running celebration (result <see cref="StreakCelebrationResult.Dismissed"/>). Returns <see langword="false"/> when none runs.</summary>
    bool DismissCurrent();

    /// <summary>Removes all waiting requests (result <see cref="StreakCelebrationResult.Dropped"/>); the running one continues.</summary>
    void ClearQueue();
}

/// <summary>Lets overlay controls make themselves available to the service. The most recently registered available host is used.</summary>
public interface IStreakCelebrationHostRegistry
{
    /// <summary>Registers a host; dispose the result to unregister (e.g. when the control is unloaded).</summary>
    IDisposable RegisterHost(IStreakCelebrationHost host);
}

/// <summary>Everything a host needs to play one celebration.</summary>
/// <param name="Data">Text, count and level from the caller.</param>
/// <param name="Timeline">Resolved animation plan.</param>
/// <param name="Options">Validated options snapshot (branding, layout, behavior).</param>
public sealed record CelebrationPlayback(StreakCelebrationData Data, CelebrationTimeline Timeline, StreakCelebrationOptions Options);

/// <summary>A UI element that can display celebrations, typically the <c>StreakCelebrationOverlay</c> control.</summary>
public interface IStreakCelebrationHost
{
    /// <summary><see langword="true"/> while the host is on screen and the app is in the foreground.</summary>
    bool IsAvailable { get; }

    /// <summary>Raised when <see cref="IsAvailable"/> changes.</summary>
    event EventHandler? AvailabilityChanged;

    /// <summary>
    /// Plays one celebration; called on the UI thread, never concurrently. Must restore the host's idle state
    /// (hidden, input-transparent, animations released) before the task completes, also on cancellation.
    /// </summary>
    /// <returns><see cref="StreakCelebrationResult.Completed"/> or <see cref="StreakCelebrationResult.Dismissed"/> (tap).</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<StreakCelebrationResult> PlayAsync(CelebrationPlayback playback, CancellationToken cancellationToken);
}

/// <summary>Runs work on the UI thread.</summary>
public interface ICelebrationDispatcher
{
    /// <summary>Invokes <paramref name="action"/> on the UI thread and returns its result.</summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}

/// <summary>Reports the OS "reduce motion" accessibility setting.</summary>
public interface IReducedMotionProvider
{
    /// <summary><see langword="true"/> when the user asked the OS to reduce motion.</summary>
    bool IsReducedMotionEnabled { get; }
}

/// <summary>Plays the celebration sound.</summary>
public interface ICelebrationSoundPlayer
{
    /// <summary>Starts playing <paramref name="soundFile"/> (a packaged raw asset). Should return once playback started.</summary>
    Task PlayAsync(string soundFile, CancellationToken cancellationToken);
}

/// <summary>Default dispatcher for environments without a UI thread (tests, console): invokes directly.</summary>
public sealed class InlineCelebrationDispatcher : ICelebrationDispatcher
{
    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }
}

/// <summary>Default when no platform provider is registered: motion is never reduced.</summary>
public sealed class NoReducedMotionProvider : IReducedMotionProvider
{
    /// <inheritdoc />
    public bool IsReducedMotionEnabled => false;
}

/// <summary>Default when no platform player is registered: plays nothing.</summary>
public sealed class SilentCelebrationSoundPlayer : ICelebrationSoundPlayer
{
    /// <inheritdoc />
    public Task PlayAsync(string soundFile, CancellationToken cancellationToken) => Task.CompletedTask;
}
