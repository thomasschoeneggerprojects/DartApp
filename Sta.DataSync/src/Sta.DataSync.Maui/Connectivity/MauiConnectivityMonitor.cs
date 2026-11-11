using MauiConnectivity = Microsoft.Maui.Networking.Connectivity;

namespace Sta.DataSync.Maui.Connectivity;

/// <summary><see cref="IConnectivityMonitor"/> backed by MAUI <see cref="MauiConnectivity"/>.</summary>
public sealed class MauiConnectivityMonitor : IConnectivityMonitor, IDisposable
{
    /// <summary>Starts listening to connectivity changes.</summary>
    public MauiConnectivityMonitor()
    {
        MauiConnectivity.Current.ConnectivityChanged += OnChanged;
    }

    /// <inheritdoc />
    public event EventHandler? ConnectivityChanged;

    /// <inheritdoc />
    public bool IsConnected => MauiConnectivity.Current.NetworkAccess == NetworkAccess.Internet;

    /// <inheritdoc />
    public bool IsUnmetered => MauiConnectivity.Current.ConnectionProfiles.Any(p => p is ConnectionProfile.WiFi or ConnectionProfile.Ethernet);

    /// <summary>Stops listening.</summary>
    public void Dispose() => MauiConnectivity.Current.ConnectivityChanged -= OnChanged;

    private void OnChanged(object? sender, ConnectivityChangedEventArgs e) => ConnectivityChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Re-raises <see cref="IDataSyncService"/> events on the UI thread for view models.</summary>
public sealed class MainThreadSyncEvents : IDisposable
{
    private readonly IDataSyncService _service;

    /// <summary>Subscribes to the service.</summary>
    public MainThreadSyncEvents(IDataSyncService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _service.StatusChanged += OnStatus;
        _service.ProgressChanged += OnProgress;
        _service.SyncCompleted += OnCompleted;
    }

    /// <summary><see cref="IDataSyncService.StatusChanged"/> on the main thread.</summary>
    public event EventHandler<SyncStatusChangedEventArgs>? StatusChanged;

    /// <summary><see cref="IDataSyncService.ProgressChanged"/> on the main thread.</summary>
    public event EventHandler<SyncProgressEventArgs>? ProgressChanged;

    /// <summary><see cref="IDataSyncService.SyncCompleted"/> on the main thread.</summary>
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    /// <summary>Unsubscribes.</summary>
    public void Dispose()
    {
        _service.StatusChanged -= OnStatus;
        _service.ProgressChanged -= OnProgress;
        _service.SyncCompleted -= OnCompleted;
    }

    private void OnStatus(object? sender, SyncStatusChangedEventArgs e) => MainThread.BeginInvokeOnMainThread(() => StatusChanged?.Invoke(this, e));

    private void OnProgress(object? sender, SyncProgressEventArgs e) => MainThread.BeginInvokeOnMainThread(() => ProgressChanged?.Invoke(this, e));

    private void OnCompleted(object? sender, SyncCompletedEventArgs e) => MainThread.BeginInvokeOnMainThread(() => SyncCompleted?.Invoke(this, e));
}
