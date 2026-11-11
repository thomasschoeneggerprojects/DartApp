namespace StreakCelebration.Models;

/// <summary>
/// Branding, timing and behavior of the celebration overlay. Configure once at startup
/// (<c>UseStreakCelebrations(options =&gt; ...)</c>); the service validates and snapshots the values.
/// </summary>
public sealed class StreakCelebrationOptions
{
    /// <summary>Smallest allowed <see cref="AnimationDuration"/>.</summary>
    public static readonly TimeSpan MinAnimationDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>Largest allowed <see cref="AnimationDuration"/>.</summary>
    public static readonly TimeSpan MaxAnimationDuration = TimeSpan.FromSeconds(3);

    /// <summary>Smallest allowed <see cref="DisplayDuration"/>.</summary>
    public static readonly TimeSpan MinDisplayDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>Largest allowed <see cref="DisplayDuration"/>.</summary>
    public static readonly TimeSpan MaxDisplayDuration = TimeSpan.FromSeconds(30);

    /// <summary>Largest allowed <see cref="Intensity"/>.</summary>
    public const double MaxIntensity = 2.0;

    /// <summary>Colors used while the app runs in its light theme.</summary>
    public CelebrationPalette LightPalette { get; set; } = CelebrationPalette.DefaultLight;

    /// <summary>Colors used while the app runs in its dark theme.</summary>
    public CelebrationPalette DarkPalette { get; set; } = CelebrationPalette.DefaultDark;

    /// <summary>Strength of the background dimming, 0 (none) to 1 (opaque). <see cref="CelebrationLevel.Normal"/> uses half of it.</summary>
    public double BackgroundOpacity { get; set; } = 0.55;

    /// <summary>Base length of the entrance animations; all phases scale with it.</summary>
    public TimeSpan AnimationDuration { get; set; } = TimeSpan.FromMilliseconds(450);

    /// <summary>How long a <see cref="CelebrationLevel.Milestone"/> celebration stays fully visible (Normal: 75 %, Record: 130 %).</summary>
    public TimeSpan DisplayDuration { get; set; } = TimeSpan.FromSeconds(2.4);

    /// <summary>Icon image (file name of a MAUI image, or an http(s) URI). <see langword="null"/> uses the built-in vector flame.</summary>
    public string? IconSource { get; set; }

    /// <summary>Effect strength from 0 (calm) to <see cref="MaxIntensity"/>; scales bounce, glow and particle count.</summary>
    public double Intensity { get; set; } = 1.0;

    /// <summary>Confetti particles for Milestone and Record celebrations.</summary>
    public bool EnableParticles { get; set; } = true;

    /// <summary>Plays <see cref="SoundFile"/> when a celebration starts. Requires <see cref="SoundFile"/>.</summary>
    public bool EnableSound { get; set; }

    /// <summary>Sound packaged as a MAUI raw asset (Resources/Raw), e.g. <c>celebration.mp3</c>.</summary>
    public string? SoundFile { get; set; }

    /// <summary>Optional different sound for <see cref="CelebrationLevel.Record"/>; falls back to <see cref="SoundFile"/>.</summary>
    public string? RecordSoundFile { get; set; }

    /// <summary>
    /// <see langword="true"/>: the overlay takes taps while visible and a tap dismisses it.
    /// <see langword="false"/>: the overlay is fully input-transparent, so the screen below stays usable.
    /// </summary>
    public bool DismissOnTap { get; set; } = true;

    /// <summary>Vertical placement of the content.</summary>
    public CelebrationPosition Position { get; set; } = CelebrationPosition.Center;

    /// <summary>Font size of the count at scale 1.</summary>
    public double CountFontSize { get; set; } = 64;

    /// <summary>Font size of the title at scale 1.</summary>
    public double TitleFontSize { get; set; } = 26;

    /// <summary>Font size of the message at scale 1.</summary>
    public double MessageFontSize { get; set; } = 16;

    /// <summary>Icon height at scale 1.</summary>
    public double IconSize { get; set; } = 112;

    /// <summary>Extra multiplier for all text and icon sizes (on top of the automatic screen-size scaling).</summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>Optional font family (a MAUI font alias) for count and title.</summary>
    public string? FontFamily { get; set; }

    /// <summary>Uses simple fades instead of motion when the OS asks for reduced motion.</summary>
    public bool RespectReducedMotion { get; set; } = true;

    /// <summary>Most requests waiting at once; on overflow the oldest waiting request is dropped.</summary>
    public int MaxQueueLength { get; set; } = 8;

    /// <summary>A request not shown within this time is expired (e.g. while the app was in the background).</summary>
    public TimeSpan QueuedLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Returns all validation errors (empty when valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (LightPalette is null) errors.Add($"{nameof(LightPalette)} is required.");
        else errors.AddRange(LightPalette.Validate(nameof(LightPalette)));
        if (DarkPalette is null) errors.Add($"{nameof(DarkPalette)} is required.");
        else errors.AddRange(DarkPalette.Validate(nameof(DarkPalette)));

        RequireRange(errors, nameof(BackgroundOpacity), BackgroundOpacity, 0, 1);
        RequireRange(errors, nameof(Intensity), Intensity, 0, MaxIntensity);
        RequireRange(errors, nameof(AnimationDuration), AnimationDuration, MinAnimationDuration, MaxAnimationDuration);
        RequireRange(errors, nameof(DisplayDuration), DisplayDuration, MinDisplayDuration, MaxDisplayDuration);

        RequirePositive(errors, nameof(CountFontSize), CountFontSize);
        RequirePositive(errors, nameof(TitleFontSize), TitleFontSize);
        RequirePositive(errors, nameof(MessageFontSize), MessageFontSize);
        RequirePositive(errors, nameof(IconSize), IconSize);
        RequireRange(errors, nameof(FontScale), FontScale, 0.5, 3);

        if (!Enum.IsDefined(Position)) errors.Add($"{nameof(Position)} '{Position}' is not a defined {nameof(CelebrationPosition)}.");
        if (EnableSound && string.IsNullOrWhiteSpace(SoundFile)) errors.Add($"{nameof(EnableSound)} requires {nameof(SoundFile)}.");
        if (MaxQueueLength < 1) errors.Add($"{nameof(MaxQueueLength)} must be at least 1.");
        if (QueuedLifetime <= TimeSpan.Zero) errors.Add($"{nameof(QueuedLifetime)} must be positive.");

        return errors;
    }

    /// <summary>Throws <see cref="ArgumentException"/> listing every problem when the options are invalid.</summary>
    public void ThrowIfInvalid()
    {
        IReadOnlyList<string> errors = Validate();
        if (errors.Count > 0) throw new ArgumentException("Invalid streak celebration options: " + string.Join(" ", errors));
    }

    /// <summary>A copy, so later changes to the configured instance cannot affect a running service.</summary>
    public StreakCelebrationOptions Clone() => (StreakCelebrationOptions)MemberwiseClone();

    /// <summary>Palette for the current theme.</summary>
    public CelebrationPalette PaletteFor(bool darkTheme) => darkTheme ? DarkPalette : LightPalette;

    /// <summary>Sound for a celebration level, or <see langword="null"/> when sound is off.</summary>
    public string? SoundFor(CelebrationLevel level)
    {
        if (!EnableSound) return null;
        return level == CelebrationLevel.Record && !string.IsNullOrWhiteSpace(RecordSoundFile) ? RecordSoundFile : SoundFile;
    }

    private static void RequireRange(List<string> errors, string name, double value, double min, double max)
    {
        if (double.IsNaN(value) || value < min || value > max) errors.Add($"{name} must be between {min} and {max}.");
    }

    private static void RequireRange(List<string> errors, string name, TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min || value > max) errors.Add($"{name} must be between {min.TotalMilliseconds} ms and {max.TotalMilliseconds} ms.");
    }

    private static void RequirePositive(List<string> errors, string name, double value)
    {
        if (double.IsNaN(value) || value <= 0) errors.Add($"{name} must be positive.");
    }
}
