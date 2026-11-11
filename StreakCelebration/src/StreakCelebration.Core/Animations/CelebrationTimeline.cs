using StreakCelebration.Models;

namespace StreakCelebration.Animations;

/// <summary>
/// The resolved animation plan for one celebration: phase durations and effect strengths for its level,
/// intensity and the reduced-motion setting. Platform-independent, so the plan itself is unit-testable;
/// the MAUI animator only executes it.
/// </summary>
public sealed record CelebrationTimeline
{
    /// <summary>Upper bound for particles, independent of intensity.</summary>
    public const int MaxParticles = 60;

    /// <summary>The level this plan was built for.</summary>
    public required CelebrationLevel Level { get; init; }

    /// <summary><see langword="true"/> when motion effects were replaced by plain fades.</summary>
    public required bool ReducedMotion { get; init; }

    /// <summary>Phase 1: overlay and backdrop fade in.</summary>
    public required TimeSpan FadeIn { get; init; }

    /// <summary>Phase 2: icon scales up with a bounce (zero = appears directly).</summary>
    public required TimeSpan IconEntrance { get; init; }

    /// <summary>Phase 3: count animates from <see cref="CountFrom"/> to the final count (zero = shown directly).</summary>
    public required TimeSpan CountUp { get; init; }

    /// <summary>Phase 4: title and message appear.</summary>
    public required TimeSpan TextReveal { get; init; }

    /// <summary>Phase 6: fully visible time.</summary>
    public required TimeSpan Hold { get; init; }

    /// <summary>Phase 7: fade out.</summary>
    public required TimeSpan FadeOut { get; init; }

    /// <summary>Fade-out used after a tap or programmatic dismissal.</summary>
    public TimeSpan DismissFadeOut => TimeSpan.FromTicks(FadeOut.Ticks / 2);

    /// <summary>Peak icon scale during the bounce (1 = no bounce).</summary>
    public required double IconOvershoot { get; init; }

    /// <summary>Size multiplier of the whole content for this level.</summary>
    public required double ContentScale { get; init; }

    /// <summary>Final backdrop opacity (0..1).</summary>
    public required double BackdropOpacity { get; init; }

    /// <summary>First value of the count animation.</summary>
    public required int CountFrom { get; init; }

    /// <summary>The final count.</summary>
    public required int CountTo { get; init; }

    /// <summary>Glow opacity at its strongest (0 = no glow).</summary>
    public required double GlowStrength { get; init; }

    /// <summary>Number of glow pulses (0 = static glow).</summary>
    public required int GlowPulses { get; init; }

    /// <summary>Confetti particles to emit (0 = none).</summary>
    public required int ParticleCount { get; init; }

    /// <summary>How long particles fly.</summary>
    public required TimeSpan ParticleDuration { get; init; }

    /// <summary>Length of the whole celebration without interruptions.</summary>
    public TimeSpan TotalDuration => FadeIn + IconEntrance + CountUp + TextReveal + Hold + FadeOut;

    /// <summary>Builds the plan. The level comes from the caller; nothing here interprets the count.</summary>
    public static CelebrationTimeline Create(StreakCelebrationData data, StreakCelebrationOptions options, bool reducedMotion)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);

        LevelProfile profile = LevelProfile.For(data.Level);
        TimeSpan unit = options.AnimationDuration;
        double intensity = options.Intensity;
        TimeSpan hold = Scale(options.DisplayDuration, profile.HoldFactor);
        double backdrop = options.BackgroundOpacity * profile.BackdropFactor;
        double glow = Math.Min(1.0, profile.Glow * intensity);

        if (reducedMotion)
        {
            TimeSpan fade = Min(Scale(unit, 0.5), TimeSpan.FromMilliseconds(200));
            return new CelebrationTimeline
            {
                Level = data.Level,
                ReducedMotion = true,
                FadeIn = fade,
                IconEntrance = TimeSpan.Zero,
                CountUp = TimeSpan.Zero,
                TextReveal = TimeSpan.Zero,
                Hold = hold,
                FadeOut = fade,
                IconOvershoot = 1.0,
                ContentScale = profile.ContentScale,
                BackdropOpacity = backdrop,
                CountFrom = data.Count,
                CountTo = data.Count,
                GlowStrength = glow * 0.6,
                GlowPulses = 0,
                ParticleCount = 0,
                ParticleDuration = TimeSpan.Zero,
            };
        }

        int particles = options.EnableParticles
            ? Math.Min(MaxParticles, (int)Math.Round(profile.Particles * intensity))
            : 0;

        return new CelebrationTimeline
        {
            Level = data.Level,
            ReducedMotion = false,
            FadeIn = Scale(unit, 0.5),
            IconEntrance = Scale(unit, 1.2),
            CountUp = data.Count == 0 ? TimeSpan.Zero : Scale(unit, profile.CountUpFactor),
            TextReveal = Scale(unit, 0.7),
            Hold = hold,
            FadeOut = Scale(unit, 0.8),
            IconOvershoot = 1.0 + ((profile.Overshoot - 1.0) * intensity),
            ContentScale = profile.ContentScale,
            BackdropOpacity = backdrop,
            CountFrom = Math.Max(0, data.Count - profile.CountUpSteps),
            CountTo = data.Count,
            GlowStrength = glow,
            GlowPulses = glow > 0 ? profile.GlowPulses : 0,
            ParticleCount = particles,
            ParticleDuration = particles > 0 ? Scale(unit, 3.6) : TimeSpan.Zero,
        };
    }

    private static TimeSpan Scale(TimeSpan value, double factor) => TimeSpan.FromTicks((long)(value.Ticks * factor));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>Per-level constants: Normal is subtle, Milestone adds glow and confetti, Record is the most intense.</summary>
    private readonly record struct LevelProfile(
        double Overshoot,
        double ContentScale,
        double BackdropFactor,
        double HoldFactor,
        double CountUpFactor,
        int CountUpSteps,
        double Glow,
        int GlowPulses,
        int Particles)
    {
        public static LevelProfile For(CelebrationLevel level) => level switch
        {
            CelebrationLevel.Record => new(1.30, 1.10, 1.0, 1.3, 2.0, 20, 1.0, 3, 36),
            CelebrationLevel.Milestone => new(1.18, 1.00, 1.0, 1.0, 1.4, 10, 0.7, 2, 18),
            _ => new(1.08, 0.85, 0.5, 0.75, 0.6, 1, 0.0, 0, 0),
        };
    }
}
