using Microsoft.Extensions.DependencyInjection.Extensions;
using SportTA.Streaks;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Fluent configuration returned by <see cref="StreakServiceCollectionExtensions.AddStreakSystem"/>.</summary>
public sealed class StreakSystemBuilder
{
    internal StreakSystemBuilder(IServiceCollection services) => Services = services;

    /// <summary>The underlying service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Replaces the default JSON repository (e.g. with the application's database or save system).</summary>
    /// <remarks>
    /// <see cref="IStreakService"/> is transient, so a scoped repository (e.g. one using an EF Core DbContext) is
    /// supported; resolve the service from a scope in that case.
    /// </remarks>
    public StreakSystemBuilder UseRepository<TRepository>(ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TRepository : class, IStreakRepository
    {
        Services.Replace(ServiceDescriptor.Describe(typeof(IStreakRepository), typeof(TRepository), lifetime));
        return this;
    }

    /// <summary>Replaces the default JSON repository using a factory.</summary>
    public StreakSystemBuilder UseRepository(Func<IServiceProvider, IStreakRepository> factory, ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.Replace(ServiceDescriptor.Describe(typeof(IStreakRepository), factory, lifetime));
        return this;
    }

    /// <summary>Keeps the JSON format but stores the bytes through a custom provider (encrypted storage, save container, ...).</summary>
    public StreakSystemBuilder UseStorageProvider<TProvider>()
        where TProvider : class, IStreakStorageProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<IStreakStorageProvider, TProvider>());
        return this;
    }

    /// <summary>Adds an event handler (registered once even if added repeatedly).</summary>
    public StreakSystemBuilder AddEventHandler<THandler>()
        where THandler : class, IStreakEventHandler
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IStreakEventHandler, THandler>());
        return this;
    }
}
