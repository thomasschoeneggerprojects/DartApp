// Sta.DataSync console sample: synchronizes a local folder with the Google Drive app data folder.
//
// Setup (see docs/google-drive-setup.md): create a "Desktop app" OAuth client and set
//   STA_DATASYNC_GOOGLE_CLIENT_ID and STA_DATASYNC_GOOGLE_CLIENT_SECRET
// Usage:
//   dotnet run -- signin | sync | status | conflicts | resolve <item> local|remote|both | signout   [--folder <dir>]
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sta.DataSync;
using Sta.DataSync.Providers.GoogleDrive.Authentication;

string command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "sync";
string folder = Option(args, "--folder") ?? Path.Combine(Environment.CurrentDirectory, "sample-data");
string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sta.DataSync.ConsoleSample");
Directory.CreateDirectory(folder);

string? clientId = Environment.GetEnvironmentVariable("STA_DATASYNC_GOOGLE_CLIENT_ID");
if (string.IsNullOrWhiteSpace(clientId))
{
    Console.Error.WriteLine("Set STA_DATASYNC_GOOGLE_CLIENT_ID (and STA_DATASYNC_GOOGLE_CLIENT_SECRET) first. See docs/google-drive-setup.md.");
    return 2;
}

var services = new ServiceCollection();
services.AddLogging(logging => logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
services.AddDataSync(options =>
{
    options.ApplicationId = "ConsoleSample";
    options.DefaultMode = SyncMode.Bidirectional;
    options.DefaultConflictStrategy = ConflictResolutionStrategy.Manual;
}, Path.Combine(appData, "sync-state.db3"));
services.AddGoogleDriveSyncProvider(o => o.ApplicationName = "Sta.DataSync.ConsoleSample");
services.AddGoogleDesktopAuthentication(o =>
{
    o.ClientId = clientId;
    o.ClientSecret = Environment.GetEnvironmentVariable("STA_DATASYNC_GOOGLE_CLIENT_SECRET");
});
services.AddSingleton<IGoogleTokenStore>(_ => CreateTokenStore(appData));
services.AddFileSyncSource("files", folder, o => o.Exclude.Add("**/*.tmp"));

await using ServiceProvider provider = services.BuildServiceProvider();
var sync = provider.GetRequiredService<IDataSyncService>();
var auth = provider.GetRequiredService<IGoogleAuthenticator>();
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    switch (command)
    {
        case "signin":
            Console.WriteLine("Opening the browser for Google sign-in...");
            await auth.SignInAsync(cancel.Token);
            Console.WriteLine("Connected.");
            break;
        case "signout":
            await auth.SignOutAsync(cancel.Token);
            Console.WriteLine("Disconnected; local tokens deleted.");
            break;
        case "status":
            await PrintStatusAsync(sync, auth, cancel.Token);
            break;
        case "conflicts":
            foreach (SyncConflict c in await sync.GetConflictsAsync(null, cancel.Token)) PrintConflict(c);
            break;
        case "resolve":
            await ResolveAsync(sync, args, cancel.Token);
            break;
        default:
            await SynchronizeAsync(sync, folder, cancel.Token);
            break;
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Canceled.");
    return 1;
}
catch (SyncException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static async Task SynchronizeAsync(IDataSyncService sync, string folder, CancellationToken cancellationToken)
{
    Console.WriteLine($"Synchronizing {folder} ...");
    var progress = new Progress<SyncProgress>(p =>
        Console.WriteLine($"  {p.CompletedItems}/{p.TotalItems}  {p.TransferredBytes:N0} bytes  {p.CurrentItem}"));
    SyncResult result = await sync.SynchronizeAsync("files", progress, cancellationToken);

    Console.WriteLine($"{result.Status}: {result.UploadedCount} uploaded, {result.DownloadedCount} downloaded, {result.DeletedCount} deleted, " +
        $"{result.SkippedCount} skipped, {result.TransferredBytes:N0} bytes in {result.Duration.TotalSeconds:0.0}s (session {result.SessionId}).");
    foreach (SyncFailure failure in result.Failures) Console.WriteLine($"  failed: {failure.ItemId ?? "(scope)"} - {failure.Message}{(failure.IsTransient ? " (will retry)" : string.Empty)}");
    foreach (SyncConflict conflict in result.Conflicts) PrintConflict(conflict);
    if (result.Status == SyncResultStatus.AuthenticationRequired) Console.WriteLine("Run 'signin' to connect your Google account.");
}

static async Task PrintStatusAsync(IDataSyncService sync, IGoogleAuthenticator auth, CancellationToken cancellationToken)
{
    SyncStatus status = await sync.GetStatusAsync(cancellationToken);
    Console.WriteLine($"Google account connected: {await auth.IsSignedInAsync(cancellationToken)}");
    foreach (SyncScopeStatus scope in status.Scopes)
    {
        Console.WriteLine($"{scope.ScopeId} ({scope.Mode}, {scope.ProviderId}): last success {scope.LastSuccessUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "never"}, " +
            $"conflicts {scope.PendingConflicts}{(scope.LastError is null ? string.Empty : ", last error: " + scope.LastError)}");
    }
}

static async Task ResolveAsync(IDataSyncService sync, string[] args, CancellationToken cancellationToken)
{
    string[] positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
    if (positional.Length < 3) throw new SyncConfigurationException("Usage: resolve <item> local|remote|both");
    ConflictResolution resolution = positional[2] switch
    {
        "local" => ConflictResolution.KeepLocal,
        "remote" => ConflictResolution.KeepRemote,
        "both" => ConflictResolution.KeepBoth,
        _ => throw new SyncConfigurationException("Choose local, remote or both."),
    };
    await sync.ResolveConflictAsync("files", positional[1], resolution, cancellationToken);
    Console.WriteLine("Decision stored; run 'sync' to apply it.");
}

static void PrintConflict(SyncConflict conflict)
    => Console.WriteLine($"  conflict: {conflict.ItemId} (local {(conflict.Local.IsDeleted ? "deleted" : conflict.Local.Length + " bytes")}, " +
        $"remote {(conflict.Remote.IsDeleted ? "deleted" : conflict.Remote.Length + " bytes")} from {conflict.Remote.DeviceId ?? "?"}) " +
        $"-> resolve {conflict.ItemId} local|remote|both");

static IGoogleTokenStore CreateTokenStore(string appData)
{
    if (OperatingSystem.IsWindows()) return new ProtectedFileTokenStore(Path.Combine(appData, "google-tokens.bin"));
    throw new PlatformNotSupportedException("This sample stores tokens with Windows DPAPI. Provide a secure IGoogleTokenStore for this platform.");
}

static string? Option(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
