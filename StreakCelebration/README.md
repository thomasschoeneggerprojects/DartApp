# StreakCelebration

Reusable, animated celebration overlay for .NET MAUI apps. It contains no game, streak or app-specific logic: the app
decides what to celebrate and at which level, the overlay displays it.

| Project | Target | Contents |
|---|---|---|
| `src/StreakCelebration.Core` | `net8.0` (no UI dependency) | `Models/` data, options, palette · `Services/` queue service and host contracts · `Animations/` timeline and particle simulation |
| `src/StreakCelebration.Maui` | `net9.0-android`, `-ios`, `-windows` | `Controls/StreakCelebrationOverlay` · `Animations/StreakCelebrationAnimator` and vector drawables · platform dispatcher, reduced motion and sound · `UseStreakCelebrations()` |
| `tests/StreakCelebration.Tests` | `net8.0` | queue, overlap, cancellation, lifecycle, cleanup, options, timeline, particles |

## Integration (3 steps)

```csharp
// 1. MauiProgram.cs
builder.UseStreakCelebrations(options =>
{
    options.DarkPalette = options.DarkPalette with { PrimaryColor = "#FF8A3D", SecondaryColor = "#F2C14E" };
    options.Position = CelebrationPosition.Center;
    options.DismissOnTap = true;
});
```

```xml
<!-- 2. Any page: the overlay as the last child of a Grid that holds the content (it is drawn on top). -->
<Grid xmlns:celebration="clr-namespace:StreakCelebration.Controls;assembly=StreakCelebration.Maui">
    <ContentPresenter Content="{Binding MainContent}" />
    <celebration:StreakCelebrationOverlay />
</Grid>
```

```csharp
// 3. Anywhere (any thread), e.g. from your streak system's milestone event:
await celebrationService.ShowAsync(new StreakCelebrationData
{
    Count = 10,
    Title = "10 STREAK!",
    Message = "UNSTOPPABLE!",
    Level = CelebrationLevel.Milestone,
}, cancellationToken);
```

The overlay registers itself with the service when loaded. Leave its `InputTransparent` at the default: it is hidden
(so never blocks input) while idle, and it switches input on an inner layout while playing. Changing `InputTransparent`
on the overlay itself makes Android re-parent the view.

## Behavior

- **Queue:** first in, first out; never two at once; at most `MaxQueueLength` waiting (the oldest is dropped).
- **Cancellation:** a waiting request is removed; a running one fades out quickly. Either way the task is cancelled.
- **Navigation:** if the hosting overlay is unloaded, the running celebration ends as `Interrupted`, and waiting ones
  play on the next available overlay. Brief unload/reload cycles (platform re-parenting) are ignored.
- **Backgrounding:** when the window stops, the running celebration ends as `Interrupted` and the queue pauses. It
  resumes in the foreground. Requests older than `QueuedLifetime` end as `Expired`.
- **Reduced motion:** with the OS setting on (Android "Remove animations", iOS "Reduce Motion", Windows animation
  effects off), only short fades are used. Reading time is unchanged.
- **Branding:** light and dark palettes in the options, or app resources named `StreakCelebration.PrimaryColor` etc.,
  which override them. A custom `IconSource` replaces the built-in vector flame. Sound plays a `Resources/Raw` file.

DartTA's integration: `DartForge.App/MauiProgram.cs`, `MainPage.xaml` and `DartForge.App/Streaks/StreakCelebrations.cs`.
