using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Sta.DataSync.Hosting;
using Sta.DataSync.Maui.Connectivity;
using Sta.DataSync.Maui.Sample.Services;
using Sta.DataSync.Providers.GoogleDrive.Authentication;

namespace Sta.DataSync.Maui.Sample.Pages;

/// <summary>
/// Settings screen state. Contains no synchronization logic: it only calls the authenticator, scheduler and service and
/// shows their state (events arrive on the main thread through <see cref="MainThreadSyncEvents"/>).
/// </summary>
public sealed class SyncSettingsViewModel : INotifyPropertyChanged
{
    private const string AutoSyncKey = "sample.autosync";
    private readonly IGoogleAuthenticator _auth;
    private readonly ISyncScheduler _scheduler;
    private readonly IDataSyncService _sync;
    private readonly SampleStore _store;
    private bool _isConnected;
    private bool _isBusy;
    private bool _autoSync = Preferences.Default.Get(AutoSyncKey, true);
    private string _state = "Idle";
    private string _lastSync = "never";
    private double _progress;
    private string? _error;
    private string _note = string.Empty;
    private string _records = "0 records";

    public SyncSettingsViewModel(IGoogleAuthenticator auth, ISyncScheduler scheduler, IDataSyncService sync, MainThreadSyncEvents events, SampleStore store)
    {
        _auth = auth;
        _scheduler = scheduler;
        _sync = sync;
        _store = store;
        ConnectCommand = new AsyncCommand(ConnectAsync, this);
        DisconnectCommand = new AsyncCommand(DisconnectAsync, this);
        SyncNowCommand = new AsyncCommand(SyncNowAsync, this);
        SaveNoteCommand = new AsyncCommand(() => _store.SaveNoteAsync(Note), this);
        AddRecordCommand = new AsyncCommand(AddRecordAsync, this);

        events.StatusChanged += (_, e) => State = Describe(e.Status.Activity);
        events.ProgressChanged += (_, e) => Progress = e.Progress.TotalItems == 0 ? 0 : (double)e.Progress.CompletedItems / e.Progress.TotalItems;
        events.SyncCompleted += async (_, e) => await OnCompletedAsync(e.Result);
        store.Reloaded += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await RefreshDataAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand ConnectCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand SyncNowCommand { get; }

    public ICommand SaveNoteCommand { get; }

    public ICommand AddRecordCommand { get; }

    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }

    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    public bool AutoSync
    {
        get => _autoSync;
        set
        {
            if (!Set(ref _autoSync, value)) return;
            Preferences.Default.Set(AutoSyncKey, value);
            if (value) _scheduler.ResumeAutomaticSync();
            else _scheduler.PauseAutomaticSync();
        }
    }

    public string State { get => _state; private set => Set(ref _state, value); }

    public string LastSync { get => _lastSync; private set => Set(ref _lastSync, value); }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public string? Error { get => _error; set => Set(ref _error, value); }

    public string Note { get => _note; set => Set(ref _note, value); }

    public string Records { get => _records; private set => Set(ref _records, value); }

    public async Task InitializeAsync()
    {
        IsConnected = await _auth.IsSignedInAsync();
        if (!AutoSync) _scheduler.PauseAutomaticSync();
        Note = await _store.LoadNoteAsync();
        await RefreshStatusAsync();
        await RefreshDataAsync();
    }

    private async Task ConnectAsync()
    {
        await _auth.SignInAsync();
        IsConnected = true;
        _scheduler.NotifyAuthenticationChanged();
    }

    private async Task DisconnectAsync()
    {
        await _auth.SignOutAsync();
        IsConnected = false;
    }

    private async Task SyncNowAsync()
    {
        Progress = 0;
        SyncResult result = await _scheduler.SyncNowAsync();
        await OnCompletedAsync(result);
    }

    private async Task AddRecordAsync()
    {
        await _store.AddRecordAsync("Training " + DateTime.Now.ToString("g", CultureInfo.CurrentCulture));
        _scheduler.NotifyLocalChange("records");
        await RefreshDataAsync();
    }

    private async Task OnCompletedAsync(SyncResult result)
    {
        Error = result.Status switch
        {
            SyncResultStatus.AuthenticationRequired => "Connect your Google account to synchronize.",
            SyncResultStatus.Failed or SyncResultStatus.PartiallySucceeded when result.Failures.Count > 0 => result.Failures[0].Message,
            _ when result.ConflictCount > 0 => $"{result.ConflictCount} conflict(s) need a decision.",
            _ => null,
        };
        await RefreshStatusAsync();
        await RefreshDataAsync();
    }

    private async Task RefreshStatusAsync()
    {
        SyncStatus status = await _sync.GetStatusAsync();
        State = Describe(status.Activity);
        LastSync = status.LastSuccessUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "never";
    }

    private async Task RefreshDataAsync()
    {
        Records = $"{await _store.CountRecordsAsync()} records";
        Note = await _store.LoadNoteAsync();
    }

    private static string Describe(SyncActivity activity) => activity switch
    {
        SyncActivity.Synchronizing => "Synchronizing...",
        SyncActivity.Paused => "Paused",
        SyncActivity.Offline => "Offline - changes are kept on this device",
        _ => "Idle",
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>Runs an async action without blocking the UI; errors are shown instead of crashing.</summary>
    private sealed class AsyncCommand(Func<Task> execute, SyncSettingsViewModel owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !owner.IsBusy;

        public async void Execute(object? parameter)
        {
            owner.IsBusy = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                owner.Error = null;
                await execute();
            }
            catch (OperationCanceledException)
            {
                owner.Error = "Canceled.";
            }
            catch (SyncException ex)
            {
                owner.Error = ex.Message;
            }
            finally
            {
                owner.IsBusy = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
