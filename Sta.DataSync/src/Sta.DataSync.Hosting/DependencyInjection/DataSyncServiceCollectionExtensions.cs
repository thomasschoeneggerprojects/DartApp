using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sta.DataSync;
using Sta.DataSync.Hosting;
using Sta.DataSync.Sources.Files;
using Sta.DataSync.Sources.Sqlite;
using Sta.DataSync.Storage.State;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of Sta.DataSync with Microsoft dependency injection.</summary>
public static class DataSyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine (<see cref="IDataSyncService"/>), the SQLite state store and shared state.
    /// <paramref name="stateDatabasePath"/> defaults to <c>{LocalApplicationData}/Sta.DataSync/{ApplicationId}/sync-state.db3</c>.
    /// Then register a provider (e.g. <c>AddGoogleDriveSyncProvider</c>) and sources (e.g. <see cref="AddFileSyncSource"/>).
    /// </summary>
    public static IServiceCollection AddDataSync(this IServiceCollection services, Action<DataSyncOptions> configure, string? stateDatabasePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<DataSyncOptions>()
            .Configure(configure)
            .Validate(o => o.Validate().Count == 0, "Invalid DataSyncOptions (ApplicationId must be a single path segment, ranges must be valid).");
        services.AddOptions<SqliteSyncStateStoreOptions>()
            .Configure<IOptions<DataSyncOptions>>((store, sync) => store.DatabasePath = stateDatabasePath ?? DefaultStatePath(sync.Value.ApplicationId));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SyncActivityState>();
        services.TryAddSingleton<ISyncStateStore, SqliteSyncStateStore>();
        services.TryAddSingleton<DataSyncService>(sp => new DataSyncService(
            sp.GetServices<SyncScopeDefinition>(),
            sp.GetRequiredService<ISyncStateStore>(),
            sp.GetRequiredService<IOptions<DataSyncOptions>>(),
            sp.GetServices<IConflictResolver>(),
            sp.GetRequiredService<SyncActivityState>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton<IDataSyncService>(sp => sp.GetRequiredService<DataSyncService>());
        return services;
    }

    /// <summary>Registers a directory as a synchronization scope.</summary>
    public static IServiceCollection AddFileSyncSource(this IServiceCollection services, string sourceId, string directory, Action<FileSyncScopeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = new FileSyncScopeOptions();
        configure?.Invoke(scope);
        var sourceOptions = new FileSyncSourceOptions { SourceId = sourceId, RootDirectory = directory, WatchForChanges = scope.WatchForChanges };
        if (scope.Include.Count > 0)
        {
            sourceOptions.Include.Clear();
            foreach (string pattern in scope.Include) sourceOptions.Include.Add(pattern);
        }

        foreach (string pattern in scope.Exclude) sourceOptions.Exclude.Add(pattern);
        ThrowIfInvalid(sourceOptions.Validate(), sourceId);
        return services.AddSyncSource(sourceId, _ => new FileSyncSource(sourceOptions), scope);
    }

    /// <summary>Registers a SQLite database as a snapshot scope (consistent backups, safe restores).</summary>
    public static IServiceCollection AddSqliteSnapshotSyncSource(this IServiceCollection services, string sourceId, string databasePath, Action<SqliteSyncScopeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = new SqliteSyncScopeOptions();
        configure?.Invoke(scope);
        var sourceOptions = new SqliteSnapshotSyncSourceOptions
        {
            SourceId = sourceId,
            DatabasePath = databasePath,
            ItemId = scope.ItemId,
            BeforeRestoreAsync = scope.BeforeRestoreAsync,
            AfterRestoreAsync = scope.AfterRestoreAsync,
        };
        ThrowIfInvalid(sourceOptions.Validate(), sourceId);
        return services.AddSyncSource(sourceId, _ => new SqliteSnapshotSyncSource(sourceOptions), scope);
    }

    /// <summary>Registers any <see cref="ISyncDataSource"/> as a scope.</summary>
    public static IServiceCollection AddSyncSource(this IServiceCollection services, string sourceId, Func<IServiceProvider, ISyncDataSource> factory, SyncScopeOptions? scope = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(factory);
        scope ??= new SyncScopeOptions();
        string scopeId = scope.ScopeId ?? sourceId;

        services.AddSingleton(sp => new SyncScopeDefinition(scopeId, factory(sp), ResolveProvider(sp, scope.ProviderId, scopeId))
        {
            Mode = scope.Mode,
            ConflictStrategy = scope.ConflictStrategy,
            DeletionPolicy = scope.DeletionPolicy,
        });
        return services;
    }

    /// <summary>Registers a resolver for <see cref="ConflictResolutionStrategy.Custom"/>.</summary>
    public static IServiceCollection AddConflictResolver<TResolver>(this IServiceCollection services)
        where TResolver : class, IConflictResolver
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConflictResolver, TResolver>());
        return services;
    }

    /// <summary>Registers the scheduler (<see cref="ISyncScheduler"/>); start it from the application or via <see cref="AddDataSyncHostedScheduler"/>.</summary>
    public static IServiceCollection AddDataSyncScheduler(this IServiceCollection services, Action<SyncScheduleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        OptionsBuilder<SyncScheduleOptions> options = services.AddOptions<SyncScheduleOptions>();
        if (configure is not null) options.Configure(configure);
        options.Validate(o => o.Validate().Count == 0, "Invalid SyncScheduleOptions.");
        services.TryAddSingleton<SyncScheduler>(sp => new SyncScheduler(
            sp.GetRequiredService<IDataSyncService>(),
            sp.GetRequiredService<IOptions<SyncScheduleOptions>>(),
            sp.GetServices<SyncScopeDefinition>(),
            sp.GetRequiredService<SyncActivityState>(),
            sp.GetService<IConnectivityMonitor>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<SyncScheduler>>()));
        services.TryAddSingleton<ISyncScheduler>(sp => sp.GetRequiredService<SyncScheduler>());
        return services;
    }

    /// <summary>Starts and stops the scheduler with a generic host (ASP.NET Core, worker services, desktop hosts).</summary>
    public static IServiceCollection AddDataSyncHostedScheduler(this IServiceCollection services, Action<SyncScheduleOptions>? configure = null)
    {
        services.AddDataSyncScheduler(configure);
        services.AddHostedService<SchedulerHostedService>();
        return services;
    }

    /// <summary>Picks the scope's provider by id, or the only registered one; never guesses between several.</summary>
    private static ISyncProvider ResolveProvider(IServiceProvider services, string? providerId, string scopeId)
    {
        List<ISyncProvider> providers = services.GetServices<ISyncProvider>().ToList();
        if (providerId is not null)
        {
            return providers.FirstOrDefault(p => p.ProviderId == providerId)
                ?? throw new SyncConfigurationException($"Scope '{scopeId}' uses provider '{providerId}', which is not registered.");
        }

        return providers.Count switch
        {
            1 => providers[0],
            0 => throw new SyncConfigurationException($"Scope '{scopeId}' has no provider; register one, e.g. AddGoogleDriveSyncProvider()."),
            _ => throw new SyncConfigurationException($"Several providers are registered; set ProviderId for scope '{scopeId}'."),
        };
    }

    private static void ThrowIfInvalid(IReadOnlyList<string> errors, string sourceId)
    {
        if (errors.Count > 0) throw new ArgumentException($"Invalid settings for source '{sourceId}': {string.Join(" ", errors)}");
    }

    private static string DefaultStatePath(string applicationId)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sta.DataSync", applicationId, "sync-state.db3");

    private sealed class SchedulerHostedService(ISyncScheduler scheduler) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => scheduler.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => scheduler.StopAsync(cancellationToken);
    }
}
