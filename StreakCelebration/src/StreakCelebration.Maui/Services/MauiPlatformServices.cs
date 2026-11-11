namespace StreakCelebration.Services;

/// <summary>Runs host calls on the MAUI main thread.</summary>
public sealed class MauiCelebrationDispatcher : ICelebrationDispatcher
{
    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return MainThread.IsMainThread ? action() : MainThread.InvokeOnMainThreadAsync(action);
    }
}

/// <summary>Reads the OS reduce-motion setting on Android, iOS/Mac Catalyst and Windows.</summary>
public sealed class PlatformReducedMotionProvider : IReducedMotionProvider
{
#if WINDOWS
    private readonly Lazy<Windows.UI.ViewManagement.UISettings> _settings = new(() => new Windows.UI.ViewManagement.UISettings());
#endif

    /// <inheritdoc />
    public bool IsReducedMotionEnabled
    {
        get
        {
#if ANDROID
            // "Remove animations" (Android accessibility) sets the animator duration scale to 0.
            Android.Content.ContentResolver? resolver = Android.App.Application.Context.ContentResolver;
            return resolver is not null
                && Android.Provider.Settings.Global.GetFloat(resolver, Android.Provider.Settings.Global.AnimatorDurationScale, 1f) == 0f;
#elif IOS || MACCATALYST
            return UIKit.UIAccessibility.IsReduceMotionEnabled;
#elif WINDOWS
            return !_settings.Value.AnimationsEnabled;
#else
            return false;
#endif
        }
    }
}
