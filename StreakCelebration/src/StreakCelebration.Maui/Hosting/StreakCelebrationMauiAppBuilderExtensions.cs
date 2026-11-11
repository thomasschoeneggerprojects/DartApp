using Microsoft.Extensions.DependencyInjection.Extensions;
using StreakCelebration.Models;
using StreakCelebration.Services;

namespace StreakCelebration.Hosting;

/// <summary>MAUI registration for the streak celebration overlay.</summary>
public static class StreakCelebrationMauiAppBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="IStreakCelebrationService"/> with the MAUI dispatcher, the platform reduced-motion setting and
    /// the platform sound player. Services registered before this call (e.g. a custom <see cref="ICelebrationSoundPlayer"/>)
    /// are kept. Then place a <c>StreakCelebrationOverlay</c> on top of your page content.
    /// </summary>
    /// <exception cref="ArgumentException">The configured options are invalid.</exception>
    public static MauiAppBuilder UseStreakCelebrations(this MauiAppBuilder builder, Action<StreakCelebrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<ICelebrationDispatcher, MauiCelebrationDispatcher>();
        builder.Services.TryAddSingleton<IReducedMotionProvider, PlatformReducedMotionProvider>();
        builder.Services.TryAddSingleton<ICelebrationSoundPlayer, MauiCelebrationSoundPlayer>();
        builder.Services.AddStreakCelebrations(configure);
        return builder;
    }
}
