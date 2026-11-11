using Microsoft.Extensions.Logging;
using Sta.DataSync;
using Sta.DataSync.Hosting;
using Sta.DataSync.Providers.GoogleDrive.Authentication;

namespace DartForge.App.Services;

/// <summary>
/// DartTA's cloud backup and sync on top of Sta.DataSync: the match and training history (<c>dartforge.db3</c>) is kept
/// in the user's Google Drive app data folder. DartTA always works offline; this only adds synchronization.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Automatic sync (scheduler) runs only while an account is connected and the user left it switched on; it
/// pauses in the background (handled by <c>UseDataSync()</c>).</item>
/// <item>The history is one SQLite snapshot. When it changed on this device and on another device, nothing is merged
/// or overwritten until the user chooses a version (<see cref="KeepVersionAsync"/>).</item>
/// <item>Disconnecting resets the sync state, so connecting a different Google account starts with a fresh comparison
/// instead of treating that account's data as changes.</item>
/// </list>
/// </remarks>
public sealed partial class CloudSync(
    IGoogleAuthenticator auth,
    ISyncScheduler scheduler,
    IDataSyncService sync,
    ISyncStateStore state,
    ILogger<CloudSync> logger)
{
    /// <summary>Scope (and remote folder) of the match and training history.</summary>
    public const string HistoryScope = "history";

    private const string AutoSyncKey = "dartta.cloudsync.auto";

    /// <summary>The underlying service (status, progress and completion events).</summary>
    public IDataSyncService Service => sync;

    /// <summary>User setting: synchronize automatically (default on).</summary>
    public bool AutoSync => Preferences.Default.Get(AutoSyncKey, true);

    public Task<bool> IsConnectedAsync() => auth.IsSignedInAsync();

    /// <summary>Starts automatic sync at app start when an account is connected. Never throws.</summary>
    public async Task StartIfConnectedAsync()
    {
        try
        {
            if (AutoSync && await auth.IsSignedInAsync()) await scheduler.StartAsync();
        }
        catch (Exception ex) when (ex is SyncException or InvalidOperationException or IOException)
        {
            LogStartFailed(logger, ex);
        }
    }

    /// <summary>Interactive Google sign-in; then synchronizes right away.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await auth.SignInAsync(cancellationToken);
        scheduler.NotifyAuthenticationChanged();
        if (AutoSync) await scheduler.StartAsync(cancellationToken);
    }

    /// <summary>Stops sync, signs out (revoking the grant) and forgets the sync state of this account.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await scheduler.StopAsync(cancellationToken);
        await auth.SignOutAsync(cancellationToken);
        foreach (string scope in sync.ScopeIds) await state.ResetScopeAsync(scope, cancellationToken);
    }

    /// <summary>Turns automatic sync on or off. Off stops the scheduler, so app resumes do not restart it.</summary>
    public async Task SetAutoSyncAsync(bool enabled)
    {
        Preferences.Default.Set(AutoSyncKey, enabled);
        if (!enabled) await scheduler.StopAsync();
        else if (await auth.IsSignedInAsync()) await scheduler.StartAsync();
    }

    /// <summary>Manual sync (also when automatic sync is off).</summary>
    public Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default) => scheduler.SyncNowAsync(null, cancellationToken);

    /// <summary>Reports a local change (saved match or training); automatic sync follows after a short delay.</summary>
    public void NotifyLocalChange() => scheduler.NotifyLocalChange(HistoryScope);

    /// <summary>History conflicts waiting for the user's decision.</summary>
    public Task<IReadOnlyList<SyncConflict>> ConflictsAsync() => sync.GetConflictsAsync(HistoryScope);

    /// <summary>Resolves history conflicts with this device's or the cloud's version, then synchronizes.</summary>
    public async Task<SyncResult> KeepVersionAsync(bool keepThisDevice, CancellationToken cancellationToken = default)
    {
        ConflictResolution decision = keepThisDevice ? ConflictResolution.KeepLocal : ConflictResolution.KeepRemote;
        foreach (SyncConflict conflict in await sync.GetConflictsAsync(HistoryScope, cancellationToken))
        {
            await sync.ResolveConflictAsync(HistoryScope, conflict.ItemId, decision, cancellationToken);
        }

        return await SyncNowAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "Starting cloud sync failed.")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);
}
