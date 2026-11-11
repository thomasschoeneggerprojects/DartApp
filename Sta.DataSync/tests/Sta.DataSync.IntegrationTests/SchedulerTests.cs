using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Sta.DataSync.Hosting;

namespace Sta.DataSync.IntegrationTests;

public sealed class SchedulerTests
{
    private static (SyncScheduler Scheduler, CountingService Service, FakeTimeProvider Time, FakeConnectivity Network) Create(Action<SyncScheduleOptions>? configure = null)
    {
        var options = new SyncScheduleOptions { SyncOnStartup = false, Interval = TimeSpan.FromMinutes(15), ChangeDebounce = TimeSpan.FromSeconds(5) };
        configure?.Invoke(options);
        var service = new CountingService();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var network = new FakeConnectivity();
        var scheduler = new SyncScheduler(service, Options.Create(options), [], new SyncActivityState(), network, time);
        return (scheduler, service, time, network);
    }

    [Fact]
    public async Task Startup_synchronizes_once()
    {
        (SyncScheduler scheduler, CountingService service, _, _) = Create(o => o.SyncOnStartup = true);

        await scheduler.StartAsync();
        await Eventually(() => service.Calls == 1);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Local_changes_are_debounced_into_one_run()
    {
        (SyncScheduler scheduler, CountingService service, FakeTimeProvider time, _) = Create();
        await scheduler.StartAsync();

        scheduler.NotifyLocalChange();
        time.Advance(TimeSpan.FromSeconds(2));
        scheduler.NotifyLocalChange();
        scheduler.NotifyLocalChange();
        await Task.Delay(50);
        Assert.Equal(0, service.Calls);

        time.Advance(TimeSpan.FromSeconds(6));
        await Eventually(() => service.Calls == 1);
        await Task.Delay(50);
        Assert.Equal(1, service.Calls);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Periodic_runs_wait_while_paused_and_catch_up_on_resume()
    {
        (SyncScheduler scheduler, CountingService service, FakeTimeProvider time, _) = Create();
        await scheduler.StartAsync();
        scheduler.PauseAutomaticSync();

        time.Advance(TimeSpan.FromMinutes(16));
        await Task.Delay(50);
        Assert.Equal(0, service.Calls);

        scheduler.ResumeAutomaticSync();
        await Eventually(() => service.Calls == 1);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Authentication_failures_stop_automatic_runs_until_sign_in()
    {
        (SyncScheduler scheduler, CountingService service, FakeTimeProvider time, _) = Create(o => o.SyncOnStartup = true);
        service.Status = SyncResultStatus.AuthenticationRequired;
        await scheduler.StartAsync();
        await Eventually(() => service.Calls == 1);

        time.Advance(TimeSpan.FromMinutes(31));
        scheduler.NotifyLocalChange();
        time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        Assert.Equal(1, service.Calls);

        service.Status = SyncResultStatus.Succeeded;
        scheduler.NotifyAuthenticationChanged();
        await Eventually(() => service.Calls == 2);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_backoff()
    {
        (SyncScheduler scheduler, CountingService service, FakeTimeProvider time, _) = Create(o =>
        {
            o.SyncOnStartup = true;
            o.RetryBaseDelay = TimeSpan.FromSeconds(30);
        });
        service.Status = SyncResultStatus.Failed;
        service.Transient = true;
        await scheduler.StartAsync();
        await Eventually(() => service.Calls == 1 && scheduler.PendingRetryUtc is not null);
        await Task.Delay(50); // let the loop start waiting for the retry time

        service.Status = SyncResultStatus.Succeeded;
        time.Advance(TimeSpan.FromSeconds(31));
        await Eventually(() => service.Calls == 2);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Offline_devices_wait_and_synchronize_when_the_connection_returns()
    {
        (SyncScheduler scheduler, CountingService service, _, FakeConnectivity network) = Create(o => o.SyncOnStartup = true);
        network.Connected = false;
        await scheduler.StartAsync();
        await Task.Delay(50);
        Assert.Equal(0, service.Calls);

        network.Connected = true;
        network.Raise();
        await Eventually(() => service.Calls == 1);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Wifi_only_skips_metered_connections_but_manual_sync_always_runs()
    {
        (SyncScheduler scheduler, CountingService service, _, FakeConnectivity network) = Create(o =>
        {
            o.SyncOnStartup = true;
            o.WifiOnly = true;
        });
        network.Unmetered = false;
        await scheduler.StartAsync();
        await Task.Delay(50);
        Assert.Equal(0, service.Calls);

        await scheduler.SyncNowAsync();
        Assert.Equal(1, service.Calls);
        await scheduler.StopAsync();
    }

    [Fact]
    public void Invalid_schedule_options_are_rejected()
    {
        Assert.Throws<SyncConfigurationException>(() => Create(o => o.Interval = TimeSpan.FromSeconds(5)));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeConnectivity : IConnectivityMonitor
    {
        public bool Connected { get; set; } = true;

        public bool Unmetered { get; set; } = true;

        public bool IsConnected => Connected;

        public bool IsUnmetered => Unmetered;

        public event EventHandler? ConnectivityChanged;

        public void Raise() => ConnectivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class CountingService : IDataSyncService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public SyncResultStatus Status { get; set; } = SyncResultStatus.Succeeded;

        public bool Transient { get; set; }

        public IReadOnlyList<string> ScopeIds => ["data"];

        public event EventHandler<SyncStatusChangedEventArgs>? StatusChanged;

        public event EventHandler<SyncProgressEventArgs>? ProgressChanged;

        public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

        public Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default) => SynchronizeAsync(null, null, cancellationToken);

        public Task<SyncResult> SynchronizeAsync(string scopeId, CancellationToken cancellationToken = default) => SynchronizeAsync(scopeId, null, cancellationToken);

        public Task<SyncResult> SynchronizeAsync(string? scopeId, IProgress<SyncProgress>? progress, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new SyncResult
            {
                Status = Status,
                SessionId = "s",
                StartedUtc = DateTimeOffset.UtcNow,
                CompletedUtc = DateTimeOffset.UtcNow,
                Failures = Status == SyncResultStatus.Failed ? [new SyncFailure("data", null, "network", Transient)] : [],
            });
        }

        public Task<SyncStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SyncStatus(SyncActivity.Idle, [], null));

        public Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(string? scopeId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SyncConflict>>([]);

        public Task ResolveConflictAsync(string scopeId, string itemId, ConflictResolution resolution, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Touch()
        {
            StatusChanged?.Invoke(this, new SyncStatusChangedEventArgs(new SyncStatus(SyncActivity.Idle, [], null)));
            ProgressChanged?.Invoke(this, new SyncProgressEventArgs(new SyncProgress(0, 0, 0, null)));
            SyncCompleted?.Invoke(this, new SyncCompletedEventArgs(null!));
        }
    }
}
