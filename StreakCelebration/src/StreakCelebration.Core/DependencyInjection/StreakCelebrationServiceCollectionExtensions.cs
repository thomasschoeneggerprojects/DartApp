using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StreakCelebration.Models;
using StreakCelebration.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the platform-independent celebration services.</summary>
public static class StreakCelebrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IStreakCelebrationService"/> and <see cref="IStreakCelebrationHostRegistry"/> (one singleton).
    /// Platform services (dispatcher, reduced motion, sound) default to no-op versions unless registered already;
    /// the MAUI package's <c>UseStreakCelebrations</c> registers the real ones.
    /// </summary>
    /// <exception cref="ArgumentException">The configured options are invalid.</exception>
    public static IServiceCollection AddStreakCelebrations(this IServiceCollection services, Action<StreakCelebrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new StreakCelebrationOptions();
        configure?.Invoke(options);
        options.ThrowIfInvalid();

        services.TryAddSingleton(options);
        services.TryAddSingleton<ICelebrationDispatcher, InlineCelebrationDispatcher>();
        services.TryAddSingleton<IReducedMotionProvider, NoReducedMotionProvider>();
        services.TryAddSingleton<ICelebrationSoundPlayer, SilentCelebrationSoundPlayer>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new StreakCelebrationService(
            sp.GetRequiredService<StreakCelebrationOptions>(),
            sp.GetRequiredService<ICelebrationDispatcher>(),
            sp.GetRequiredService<IReducedMotionProvider>(),
            sp.GetRequiredService<ICelebrationSoundPlayer>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<StreakCelebrationService>>()));
        services.TryAddSingleton<IStreakCelebrationService>(sp => sp.GetRequiredService<StreakCelebrationService>());
        services.TryAddSingleton<IStreakCelebrationHostRegistry>(sp => sp.GetRequiredService<StreakCelebrationService>());
        return services;
    }
}
