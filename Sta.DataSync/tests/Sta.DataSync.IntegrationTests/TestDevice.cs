using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sta.DataSync.Providers.GoogleDrive.Services;
using Sta.DataSync.Testing;

namespace Sta.DataSync.IntegrationTests;

/// <summary>A simulated installation (e.g. Windows PC or Android phone) with its own files, database and state.</summary>
internal sealed class TestDevice : IDisposable
{
    public TestDevice(FakeDriveServer server, string name, Action<IServiceCollection>? configure = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "sta-datasync-it", Guid.NewGuid().ToString("N"), name);
        Files = Path.Combine(Root, "files");
        DatabasePath = Path.Combine(Root, "app.db3");
        StatePath = Path.Combine(Root, "state", "sync-state.db3");
        Directory.CreateDirectory(Files);
        Server = server;
        Services = Build(configure);
        Sync = Services.GetRequiredService<IDataSyncService>();
    }

    public string Root { get; }

    public string Files { get; }

    public string DatabasePath { get; }

    public string StatePath { get; }

    public FakeDriveServer Server { get; }

    public ServiceProvider Services { get; private set; }

    public IDataSyncService Sync { get; private set; }

    public string File(string relative) => Path.Combine(Files, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string text)
    {
        string path = File(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        // Distinct timestamps even on coarse file systems.
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
    }

    public string? Read(string relative) => System.IO.File.Exists(File(relative)) ? System.IO.File.ReadAllText(File(relative)) : null;

    /// <summary>Simulates an app restart: new service provider on the same files and state database.</summary>
    public void Restart(Action<IServiceCollection>? configure = null)
    {
        Services.Dispose();
        SqliteConnection.ClearAllPools();
        Services = Build(configure);
        Sync = Services.GetRequiredService<IDataSyncService>();
    }

    public void Dispose()
    {
        Services.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true);
        }
        catch (IOException)
        {
            // Temp files still in use; the OS cleans up.
        }
    }

    private ServiceProvider Build(Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection();
        services.AddDataSync(o =>
        {
            o.ApplicationId = "DartTA";
            o.CommitVerificationDelay = TimeSpan.Zero;
            o.Retry = new SyncRetryOptions { MaxAttempts = 2, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero };
        }, StatePath);
        services.AddSingleton<ISyncProvider>(new GoogleDriveSyncProvider(new StaticTokenProvider(), Options.Create(new GoogleDriveProviderOptions()), null, Server.ClientFactory));
        services.AddFileSyncSource("settings", Files, o => o.Exclude.Add("**/*.log"));
        services.AddSqliteSnapshotSyncSource("database", DatabasePath);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
