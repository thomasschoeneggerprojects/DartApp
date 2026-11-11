using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SportTA.Streaks;
using SportTA.Streaks.Internal;
using SportTA.Streaks.Storage;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the streak system in an <see cref="IServiceCollection"/>.</summary>
public static class StreakServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IStreakService"/>, <see cref="StreakRegistry"/> and the default JSON persistence.
    /// </summary>
    /// <remarks>
    /// <para>The default repository is registered with <c>TryAdd</c> and created lazily, so it is replaced cleanly by
    /// <see cref="StreakSystemBuilder.UseRepository{TRepository}"/>, by registering an <see cref="IStreakRepository"/>
    /// before this call, or by a plain <c>AddSingleton&lt;IStreakRepository, T&gt;()</c> afterwards (last registration wins).
    /// <see cref="StreakOptions.StorageDirectory"/> is only required when the default JSON storage is actually used.</para>
    /// <para>A <see cref="TimeProvider"/> registered by the application is used; otherwise <see cref="TimeProvider.System"/>.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The streak system was already added.</exception>
    public static StreakSystemBuilder AddStreakSystem(this IServiceCollection services, Action<StreakOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(StreakOptions)))
        {
            throw new InvalidOperationException("AddStreakSystem has already been called for this service collection.");
        }

        var options = new StreakOptions();
        configure?.Invoke(options);
        options.Validate();
        var registry = new StreakRegistry(options.Definitions);

        services.AddSingleton(options);
        services.AddSingleton(registry);
        services.AddSingleton(new KeyedAsyncLock());
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IStreakStorageProvider>(_ => new FileStreakStorageProvider(
            options.StorageDirectory ?? throw new InvalidOperationException(
                "StreakOptions.StorageDirectory must be set to the application's data directory when the default JSON storage is used. " +
                "Alternatively register a custom IStreakRepository or IStreakStorageProvider."),
            options.StorageLockTimeout));

        services.TryAddSingleton<IStreakRepository>(sp => new JsonStreakRepository(
            sp.GetRequiredService<IStreakStorageProvider>(),
            options.CorruptDataHandling,
            sp.GetService<ILogger<JsonStreakRepository>>()));

        services.TryAddTransient<IStreakService>(sp => new StreakService(
            sp.GetRequiredService<IStreakRepository>(),
            sp.GetRequiredService<StreakRegistry>(),
            options,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetServices<IStreakEventHandler>(),
            sp.GetService<ILogger<StreakService>>(),
            sp.GetRequiredService<KeyedAsyncLock>()));

        return new StreakSystemBuilder(services);
    }
}
