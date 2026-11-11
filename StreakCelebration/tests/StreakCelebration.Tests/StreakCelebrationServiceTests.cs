using Microsoft.Extensions.Time.Testing;
using StreakCelebration.Models;
using StreakCelebration.Services;

namespace StreakCelebration.Tests;

public sealed class QueueTests
{
    [Fact]
    public async Task Requests_are_shown_one_after_another_in_order()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost();
        service.RegisterHost(host);

        Task<StreakCelebrationResult> first = service.ShowAsync(Celebrations.Data("first"));
        Task<StreakCelebrationResult> second = service.ShowAsync(Celebrations.Data("second"));
        Task<StreakCelebrationResult> third = service.ShowAsync(Celebrations.Data("third"));

        await Wait.Until(() => host.Calls.Count == 1, "first celebration");
        await Wait.Briefly();
        Assert.Single(host.Calls);
        Assert.Equal(2, service.PendingCount);
        Assert.True(service.IsShowing);

        host.Calls[0].Finish();
        await Wait.Until(() => host.Calls.Count == 2, "second celebration");
        host.Calls[1].Finish();
        await Wait.Until(() => host.Calls.Count == 3, "third celebration");
        host.Calls[2].Finish();

        Assert.Equal(StreakCelebrationResult.Completed, await first);
        Assert.Equal(StreakCelebrationResult.Completed, await second);
        Assert.Equal(StreakCelebrationResult.Completed, await third);
        Assert.Equal(["first", "second", "third"], host.Calls.Select(c => c.Title));
    }

    [Fact]
    public async Task Rapid_consecutive_requests_never_overlap()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions { MaxQueueLength = 50 });
        var host = new FakeHost { AutoComplete = StreakCelebrationResult.Completed };
        service.RegisterHost(host);

        // Requests arrive from many threads at once.
        StreakCelebrationResult[] results = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => Task.Run(() => service.ShowAsync(Celebrations.Data($"#{i}")))));

        Assert.All(results, r => Assert.Equal(StreakCelebrationResult.Completed, r));
        Assert.Equal(40, host.Calls.Count);
        Assert.Equal(1, host.MaxConcurrent);
    }

    [Fact]
    public async Task Overflow_drops_the_oldest_waiting_request()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions { MaxQueueLength = 2 });
        var host = new FakeHost();
        service.RegisterHost(host);

        Task<StreakCelebrationResult> playing = service.ShowAsync(Celebrations.Data("playing"));
        await Wait.Until(() => host.Calls.Count == 1, "first celebration");
        Task<StreakCelebrationResult> oldest = service.ShowAsync(Celebrations.Data("oldest"));
        Task<StreakCelebrationResult> middle = service.ShowAsync(Celebrations.Data("middle"));
        Task<StreakCelebrationResult> newest = service.ShowAsync(Celebrations.Data("newest"));

        Assert.Equal(StreakCelebrationResult.Dropped, await oldest);
        Assert.Equal(2, service.PendingCount);

        host.Calls[0].Finish();
        await Wait.Until(() => host.Calls.Count == 2, "middle");
        host.Calls[1].Finish();
        await Wait.Until(() => host.Calls.Count == 3, "newest");
        host.Calls[2].Finish();
        await Task.WhenAll(playing, middle, newest);
        Assert.Equal(["playing", "middle", "newest"], host.Calls.Select(c => c.Title));
    }

    [Fact]
    public async Task ClearQueue_drops_waiting_requests_but_keeps_the_running_one()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost();
        service.RegisterHost(host);

        Task<StreakCelebrationResult> running = service.ShowAsync(Celebrations.Data("running"));
        await Wait.Until(() => host.Calls.Count == 1, "running");
        Task<StreakCelebrationResult> waiting = service.ShowAsync(Celebrations.Data("waiting"));

        service.ClearQueue();

        Assert.Equal(StreakCelebrationResult.Dropped, await waiting);
        host.Calls[0].Finish();
        Assert.Equal(StreakCelebrationResult.Completed, await running);
        await Wait.Briefly();
        Assert.Single(host.Calls);
    }

    [Fact]
    public async Task Invalid_data_is_rejected_before_queueing()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ShowAsync(new StreakCelebrationData { Title = "", Count = 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ShowAsync(new StreakCelebrationData { Title = "x", Count = -1 }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ShowAsync(null!));
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task Host_calls_go_through_the_dispatcher_with_the_resolved_plan()
    {
        var dispatcher = new CountingDispatcher();
        using var service = new StreakCelebrationService(new StreakCelebrationOptions(), dispatcher);
        var host = new FakeHost { AutoComplete = StreakCelebrationResult.Completed };
        service.RegisterHost(host);

        await service.ShowAsync(Celebrations.Data("record", 12, CelebrationLevel.Record));

        Assert.Equal(1, dispatcher.Calls);
        CelebrationPlayback playback = host.Calls[0].Playback;
        Assert.Equal(CelebrationLevel.Record, playback.Timeline.Level);
        Assert.Equal(12, playback.Timeline.CountTo);
        Assert.Same(service.Options, playback.Options);
    }

    [Fact]
    public async Task Host_failure_faults_only_that_request()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost { Failure = new InvalidOperationException("boom") };
        service.RegisterHost(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ShowAsync(Celebrations.Data("broken")));

        host.Failure = null;
        host.AutoComplete = StreakCelebrationResult.Completed;
        Assert.Equal(StreakCelebrationResult.Completed, await service.ShowAsync(Celebrations.Data("next")));
    }
}

public sealed class CancellationTests
{
    [Fact]
    public async Task Cancelling_a_waiting_request_removes_it_without_showing_it()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost();
        service.RegisterHost(host);
        using var cts = new CancellationTokenSource();

        Task<StreakCelebrationResult> first = service.ShowAsync(Celebrations.Data("first"));
        await Wait.Until(() => host.Calls.Count == 1, "first");
        Task<StreakCelebrationResult> cancelled = service.ShowAsync(Celebrations.Data("cancelled"), cts.Token);
        Task<StreakCelebrationResult> third = service.ShowAsync(Celebrations.Data("third"));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, service.PendingCount);

        host.Calls[0].Finish();
        await Wait.Until(() => host.Calls.Count == 2, "third");
        host.Calls[1].Finish();
        await Task.WhenAll(first, third);
        Assert.Equal(["first", "third"], host.Calls.Select(c => c.Title));
    }

    [Fact]
    public async Task Cancelling_the_running_celebration_stops_it_and_continues_with_the_next()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost();
        service.RegisterHost(host);
        using var cts = new CancellationTokenSource();

        Task<StreakCelebrationResult> running = service.ShowAsync(Celebrations.Data("running"), cts.Token);
        Task<StreakCelebrationResult> next = service.ShowAsync(Celebrations.Data("next"));
        await Wait.Until(() => host.Calls.Count == 1, "running");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(host.Calls[0].Token.IsCancellationRequested);
        await Wait.Until(() => host.Calls.Count == 2, "next");
        host.Calls[1].Finish();
        Assert.Equal(StreakCelebrationResult.Completed, await next);
    }

    [Fact]
    public async Task An_already_cancelled_token_is_never_queued()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost();
        service.RegisterHost(host);

        Task<StreakCelebrationResult> task = service.ShowAsync(Celebrations.Data("x"), new CancellationToken(canceled: true));

        Assert.True(task.IsCanceled);
        await Wait.Briefly();
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task DismissCurrent_ends_the_running_celebration_as_dismissed()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost();
        service.RegisterHost(host);

        Assert.False(service.DismissCurrent());
        Task<StreakCelebrationResult> running = service.ShowAsync(Celebrations.Data("running"));
        await Wait.Until(() => host.Calls.Count == 1, "running");

        Assert.True(service.DismissCurrent());
        Assert.Equal(StreakCelebrationResult.Dismissed, await running);
        Assert.False(service.IsShowing);
    }

    [Fact]
    public async Task Tap_dismissal_reported_by_the_host_is_passed_through()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost { AutoComplete = StreakCelebrationResult.Dismissed };
        service.RegisterHost(host);

        Assert.Equal(StreakCelebrationResult.Dismissed, await service.ShowAsync(Celebrations.Data("tapped")));
    }
}

public sealed class LifecycleTests
{
    [Fact]
    public async Task Requests_wait_while_no_host_is_available_and_play_when_one_appears()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost(available: false);
        service.RegisterHost(host);

        Task<StreakCelebrationResult> task = service.ShowAsync(Celebrations.Data("waiting"));
        await Wait.Briefly();
        Assert.Empty(host.Calls);
        Assert.Equal(1, service.PendingCount);

        host.SetAvailable(true);
        await Wait.Until(() => host.Calls.Count == 1, "celebration after resume");
        host.Calls[0].Finish();
        Assert.Equal(StreakCelebrationResult.Completed, await task);
    }

    [Fact]
    public async Task Backgrounding_interrupts_the_running_celebration_and_pauses_the_queue()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost();
        service.RegisterHost(host);

        Task<StreakCelebrationResult> running = service.ShowAsync(Celebrations.Data("running"));
        Task<StreakCelebrationResult> queued = service.ShowAsync(Celebrations.Data("queued"));
        await Wait.Until(() => host.Calls.Count == 1, "running");

        host.SetAvailable(false); // app moved to the background

        Assert.Equal(StreakCelebrationResult.Interrupted, await running);
        await Wait.Briefly();
        Assert.Single(host.Calls);
        Assert.Equal(1, service.PendingCount);

        host.SetAvailable(true); // back in the foreground
        await Wait.Until(() => host.Calls.Count == 2, "queued after resume");
        host.Calls[1].Finish();
        Assert.Equal(StreakCelebrationResult.Completed, await queued);
    }

    [Fact]
    public async Task Navigation_away_interrupts_and_the_next_request_uses_the_remaining_host()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var shellHost = new FakeHost(name: "shell");
        var pageHost = new FakeHost(name: "page");
        service.RegisterHost(shellHost);
        IDisposable pageRegistration = service.RegisterHost(pageHost);

        Task<StreakCelebrationResult> running = service.ShowAsync(Celebrations.Data("running"));
        Task<StreakCelebrationResult> next = service.ShowAsync(Celebrations.Data("next"));
        await Wait.Until(() => pageHost.Calls.Count == 1, "most recent host is used");

        pageRegistration.Dispose(); // page unloaded

        Assert.Equal(StreakCelebrationResult.Interrupted, await running);
        await Wait.Until(() => shellHost.Calls.Count == 1, "next on the remaining host");
        shellHost.Calls[0].Finish();
        Assert.Equal(StreakCelebrationResult.Completed, await next);
        Assert.Equal(1, service.HostCount);
    }

    [Fact]
    public async Task Requests_not_shown_in_time_expire()
    {
        var time = new FakeTimeProvider();
        using var service = new StreakCelebrationService(
            new StreakCelebrationOptions { QueuedLifetime = TimeSpan.FromSeconds(30) }, timeProvider: time);
        var host = new FakeHost(available: false);
        service.RegisterHost(host);

        Task<StreakCelebrationResult> task = service.ShowAsync(Celebrations.Data("stale"));
        await Wait.Briefly();
        time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(StreakCelebrationResult.Expired, await task.WaitAsync(TimeSpan.FromSeconds(5)));
        host.SetAvailable(true);
        await Wait.Briefly();
        Assert.Empty(host.Calls);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task Registering_the_same_host_twice_does_not_duplicate_it()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost { AutoComplete = StreakCelebrationResult.Completed };

        service.RegisterHost(host);
        IDisposable second = service.RegisterHost(host);
        Assert.Equal(1, service.HostCount);

        await service.ShowAsync(Celebrations.Data("once"));
        Assert.Single(host.Calls);

        second.Dispose();
        second.Dispose();
        Assert.Equal(0, service.HostCount);
    }
}

public sealed class CleanupTests
{
    [Fact]
    public async Task Service_is_idle_after_all_celebrations_finished()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost { AutoComplete = StreakCelebrationResult.Completed };
        service.RegisterHost(host);

        await Task.WhenAll(service.ShowAsync(Celebrations.Data("a")), service.ShowAsync(Celebrations.Data("b")));

        Assert.False(service.IsShowing);
        Assert.Equal(0, service.PendingCount);
        // Playback tokens are released: the host's last token is no longer cancelable through the service.
        Assert.False(host.Calls[^1].Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_interrupts_the_running_celebration_drops_the_queue_and_rejects_new_requests()
    {
        var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost();
        service.RegisterHost(host);

        Task<StreakCelebrationResult> running = service.ShowAsync(Celebrations.Data("running"));
        Task<StreakCelebrationResult> waiting = service.ShowAsync(Celebrations.Data("waiting"));
        await Wait.Until(() => host.Calls.Count == 1, "running");

        service.Dispose();

        Assert.Equal(StreakCelebrationResult.Interrupted, await running);
        Assert.Equal(StreakCelebrationResult.Dropped, await waiting);
        Assert.Equal(0, service.HostCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ShowAsync(Celebrations.Data("late")));
        Assert.Throws<ObjectDisposedException>(() => service.RegisterHost(new FakeHost()));
        service.Dispose(); // idempotent
    }

    [Fact]
    public async Task Unregistered_hosts_no_longer_receive_availability_callbacks()
    {
        using var service = new StreakCelebrationService(new StreakCelebrationOptions());
        var host = new FakeHost(available: false);
        IDisposable registration = service.RegisterHost(host);
        Task<StreakCelebrationResult> task = service.ShowAsync(Celebrations.Data("x"));

        registration.Dispose();
        host.SetAvailable(true);
        await Wait.Briefly();

        Assert.Empty(host.Calls);
        Assert.Equal(1, service.PendingCount);
        service.ClearQueue();
        Assert.Equal(StreakCelebrationResult.Dropped, await task);
    }
}

public sealed class SoundAndMotionTests
{
    [Fact]
    public async Task Sound_plays_per_level_when_enabled()
    {
        var sound = new RecordingSoundPlayer();
        using var service = new StreakCelebrationService(
            new StreakCelebrationOptions { EnableSound = true, SoundFile = "pop.mp3", RecordSoundFile = "fanfare.mp3" },
            sound: sound);
        var host = new FakeHost { AutoComplete = StreakCelebrationResult.Completed };
        service.RegisterHost(host);

        await service.ShowAsync(Celebrations.Data("normal"));
        await service.ShowAsync(Celebrations.Data("record", level: CelebrationLevel.Record));

        await Wait.Until(() => sound.Played.Count == 2, "two sounds");
        Assert.Equal(["pop.mp3", "fanfare.mp3"], sound.Played);
    }

    [Fact]
    public async Task Sound_failure_does_not_affect_the_celebration()
    {
        var sound = new RecordingSoundPlayer { Fail = true };
        using var service = new StreakCelebrationService(
            new StreakCelebrationOptions { EnableSound = true, SoundFile = "pop.mp3" }, sound: sound);
        service.RegisterHost(new FakeHost { AutoComplete = StreakCelebrationResult.Completed });

        Assert.Equal(StreakCelebrationResult.Completed, await service.ShowAsync(Celebrations.Data("x")));
    }

    [Fact]
    public async Task No_sound_when_disabled()
    {
        var sound = new RecordingSoundPlayer();
        using var service = new StreakCelebrationService(new StreakCelebrationOptions { SoundFile = "pop.mp3" }, sound: sound);
        service.RegisterHost(new FakeHost { AutoComplete = StreakCelebrationResult.Completed });

        await service.ShowAsync(Celebrations.Data("x"));
        await Wait.Briefly();
        Assert.Empty(sound.Played);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Reduced_motion_follows_the_OS_setting_when_respected(bool osSetting, bool respect, bool expectReduced)
    {
        using var service = new StreakCelebrationService(
            new StreakCelebrationOptions { RespectReducedMotion = respect },
            reducedMotion: new FixedReducedMotion(osSetting));
        var host = new FakeHost { AutoComplete = StreakCelebrationResult.Completed };
        service.RegisterHost(host);

        await service.ShowAsync(Celebrations.Data("x", level: CelebrationLevel.Record));

        Assert.Equal(expectReduced, host.Calls[0].Playback.Timeline.ReducedMotion);
    }
}
