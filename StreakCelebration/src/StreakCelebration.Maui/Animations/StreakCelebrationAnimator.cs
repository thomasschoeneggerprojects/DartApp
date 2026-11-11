using System.Globalization;

namespace StreakCelebration.Animations;

/// <summary>The overlay elements the animator drives.</summary>
internal sealed record OverlayParts(
    VisualElement Owner,
    VisualElement Backdrop,
    VisualElement Stage,
    VisualElement IconHost,
    GraphicsView IconCanvas,
    FlameIconDrawable Icon,
    Label CountLabel,
    Label TitleLabel,
    Label MessageLabel,
    GraphicsView ParticleCanvas,
    ParticleDrawable ParticleRenderer,
    ParticleField Particles);

/// <summary>
/// Executes a <see cref="CelebrationTimeline"/> with native MAUI animations (<c>FadeTo</c>, <c>ScaleTo</c>, <c>Animate</c>).
/// All members must be called on the UI thread. Every animation can be aborted with <see cref="AbortAll"/>, and
/// <see cref="Reset"/> returns all elements to their idle state.
/// </summary>
internal sealed class StreakCelebrationAnimator(OverlayParts parts)
{
    private const string CountAnimation = "StreakCelebration.Count";
    private const string GlowAnimation = "StreakCelebration.Glow";
    private const string ParticleAnimation = "StreakCelebration.Particles";
    private const uint FrameRate = 16; // ~60 FPS

    private int _shownCount = -1;
    private int _countFrom;
    private int _countTo;
    private float _particleSeconds;
    private float _glowStrength;
    private int _glowPulses;

    // Frame callbacks are created once, so no delegate is allocated per animation or per frame.
    private Action<double>? _countStep;
    private Action<double>? _glowStep;
    private Action<double>? _particleStep;
    private Action<double, bool>? _glowFinished;
    private Action<double, bool>? _particlesFinished;

    /// <summary>Distance the content slides in from (positive: from below).</summary>
    public double EntranceOffset { get; set; } = 16;

    /// <summary>Phases 1-5: fade in, icon bounce, count-up, text reveal, glow and particles.</summary>
    public async Task RunEntranceAsync(CelebrationTimeline timeline, CancellationToken cancellationToken)
    {
        PrepareStart(timeline);

        // 1. Fade in the backdrop and content.
        await Task.WhenAll(
            FadeAsync(parts.Backdrop, timeline.BackdropOpacity, timeline.FadeIn, Easing.CubicOut),
            FadeAsync(parts.Stage, 1, timeline.FadeIn, Easing.CubicOut),
            TranslateYAsync(parts.Stage, 0, timeline.FadeIn, Easing.CubicOut));
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Icon bounce; glow and particles start at the peak of the bounce (5.).
        await IconEntranceAsync(timeline, cancellationToken);

        // 3. Count up, with a small pop at the end.
        await CountUpAsync(timeline, cancellationToken);

        // 4. Title, then message.
        await RevealTextAsync(timeline.TextReveal, cancellationToken);
    }

    /// <summary>Phase 6: keeps the celebration on screen.</summary>
    public Task HoldAsync(TimeSpan hold, CancellationToken cancellationToken)
        => hold <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(hold, cancellationToken);

    /// <summary>Phase 7: fades everything out. Particles keep falling while the content fades.</summary>
    public Task FadeOutAsync(TimeSpan duration) => Task.WhenAll(
        FadeAsync(parts.Stage, 0, duration, Easing.CubicIn),
        FadeAsync(parts.Backdrop, 0, duration, Easing.CubicIn),
        ScaleAsync(parts.Stage, 0.96, duration, Easing.CubicIn));

    /// <summary>Stops every running animation; awaiting callers resume immediately.</summary>
    public void AbortAll()
    {
        parts.Owner.AbortAnimation(CountAnimation);
        parts.Owner.AbortAnimation(GlowAnimation);
        parts.Owner.AbortAnimation(ParticleAnimation);
        parts.Backdrop.CancelAnimations();
        parts.Stage.CancelAnimations();
        parts.IconHost.CancelAnimations();
        parts.CountLabel.CancelAnimations();
        parts.TitleLabel.CancelAnimations();
        parts.MessageLabel.CancelAnimations();
    }

    /// <summary>Phase 8: releases particles and restores the idle visual state.</summary>
    public void Reset()
    {
        parts.Backdrop.Opacity = 0;
        parts.Stage.Opacity = 0;
        parts.Stage.Scale = 1;
        parts.Stage.TranslationY = 0;
        parts.IconHost.Scale = 1;
        parts.CountLabel.Scale = 1;
        parts.TitleLabel.Opacity = 0;
        parts.TitleLabel.TranslationY = 0;
        parts.MessageLabel.Opacity = 0;
        parts.MessageLabel.TranslationY = 0;
        parts.Icon.GlowAlpha = 0;
        parts.Icon.GlowScale = 1;
        parts.Particles.Clear();
        parts.IconCanvas.Invalidate();
        parts.ParticleCanvas.Invalidate();
        // No stale number from the previous celebration may show while the next one fades in.
        parts.CountLabel.Text = string.Empty;
        _shownCount = -1;
    }

    /// <summary>Shows <paramref name="value"/>; formats only when the number changes.</summary>
    public void SetCount(int value)
    {
        if (value == _shownCount) return;
        _shownCount = value;
        parts.CountLabel.Text = value.ToString("N0", CultureInfo.CurrentCulture);
    }

    private void PrepareStart(CelebrationTimeline timeline)
    {
        AbortAll();
        Reset();
        bool motion = !timeline.ReducedMotion;
        parts.Stage.TranslationY = motion ? EntranceOffset : 0;
        parts.Stage.Scale = 1;
        parts.IconHost.Scale = motion && timeline.IconEntrance > TimeSpan.Zero ? 0.3 : 1;
        parts.TitleLabel.TranslationY = motion ? 10 : 0;
        parts.MessageLabel.TranslationY = motion ? 10 : 0;
        SetCount(timeline.CountUp > TimeSpan.Zero ? timeline.CountFrom : timeline.CountTo);
    }

    private async Task IconEntranceAsync(CelebrationTimeline timeline, CancellationToken cancellationToken)
    {
        if (timeline.IconEntrance <= TimeSpan.Zero)
        {
            parts.IconHost.Scale = 1;
            StartGlow(timeline);
            StartParticles(timeline);
            return;
        }

        TimeSpan up = TimeSpan.FromTicks(timeline.IconEntrance.Ticks * 6 / 10);
        await ScaleAsync(parts.IconHost, timeline.IconOvershoot, up, Easing.CubicOut);
        cancellationToken.ThrowIfCancellationRequested();

        StartGlow(timeline);
        StartParticles(timeline);

        await ScaleAsync(parts.IconHost, 1, timeline.IconEntrance - up, Easing.SpringOut);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task CountUpAsync(CelebrationTimeline timeline, CancellationToken cancellationToken)
    {
        if (timeline.CountUp <= TimeSpan.Zero || timeline.CountFrom == timeline.CountTo)
        {
            SetCount(timeline.CountTo);
            return;
        }

        _countFrom = timeline.CountFrom;
        _countTo = timeline.CountTo;
        _countStep ??= v => SetCount(_countFrom + (int)Math.Round((_countTo - _countFrom) * v));
        await AnimateAsync(CountAnimation, _countStep, timeline.CountUp, Easing.CubicOut);
        SetCount(timeline.CountTo);
        cancellationToken.ThrowIfCancellationRequested();

        await ScaleAsync(parts.CountLabel, 1.12, TimeSpan.FromMilliseconds(90), Easing.CubicOut);
        await ScaleAsync(parts.CountLabel, 1, TimeSpan.FromMilliseconds(140), Easing.CubicIn);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RevealTextAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
        {
            parts.TitleLabel.Opacity = 1;
            parts.MessageLabel.Opacity = 1;
            return;
        }

        Task title = Task.WhenAll(
            FadeAsync(parts.TitleLabel, 1, duration, Easing.CubicOut),
            TranslateYAsync(parts.TitleLabel, 0, duration, Easing.CubicOut));
        await Task.Delay(TimeSpan.FromTicks(duration.Ticks * 3 / 10), cancellationToken);
        await Task.WhenAll(
            title,
            FadeAsync(parts.MessageLabel, 1, duration, Easing.CubicOut),
            TranslateYAsync(parts.MessageLabel, 0, duration, Easing.CubicOut));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void StartGlow(CelebrationTimeline timeline)
    {
        _glowStrength = (float)timeline.GlowStrength;
        if (_glowStrength <= 0) return;

        if (timeline.GlowPulses <= 0)
        {
            SetGlow(0.6f, 1f);
            return;
        }

        _glowPulses = timeline.GlowPulses;
        _glowStep ??= v =>
        {
            // Pulses between 45 % and 100 % strength; settles at 60 % when done.
            double wave = (Math.Sin((v * _glowPulses * 2 * Math.PI) - (Math.PI / 2)) * 0.5) + 0.5;
            SetGlow((float)(0.45 + (0.55 * wave)), 0.9f + (0.2f * (float)wave));
        };
        _glowFinished ??= (_, cancelled) =>
        {
            if (!cancelled) SetGlow(0.6f, 1f);
        };
        parts.Owner.Animate(GlowAnimation, _glowStep, FrameRate, ToMs(TimeSpan.FromMilliseconds(700 * _glowPulses)), Easing.Linear, _glowFinished);
    }

    private void SetGlow(float level, float scale)
    {
        parts.Icon.GlowAlpha = _glowStrength * level;
        parts.Icon.GlowScale = scale;
        parts.IconCanvas.Invalidate();
    }

    private void StartParticles(CelebrationTimeline timeline)
    {
        if (timeline.ParticleCount <= 0) return;

        parts.ParticleRenderer.Origin = IconCenterInOwner();
        parts.Particles.Burst(
            timeline.ParticleCount,
            timeline.ParticleDuration,
            spread: Math.Max(0.6f, parts.Icon.FlameSize / 112f),
            colorCount: parts.ParticleRenderer.ColorCount,
            seed: Environment.TickCount);
        _particleSeconds = (float)timeline.ParticleDuration.TotalSeconds;
        _particleStep ??= v =>
        {
            parts.Particles.Update((float)v * _particleSeconds);
            parts.ParticleCanvas.Invalidate();
        };
        _particlesFinished ??= (_, _) =>
        {
            parts.Particles.Clear();
            parts.ParticleCanvas.Invalidate();
        };
        parts.Owner.Animate(ParticleAnimation, _particleStep, FrameRate, ToMs(timeline.ParticleDuration), Easing.Linear, _particlesFinished);
    }

    private PointF IconCenterInOwner()
    {
        double x = parts.IconHost.Width / 2;
        double y = parts.IconHost.Height / 2;
        Element? element = parts.IconHost;
        while (element is VisualElement visual && element != parts.Owner)
        {
            x += visual.X + visual.TranslationX;
            y += visual.Y + visual.TranslationY;
            element = element.Parent;
        }

        return new PointF((float)x, (float)y);
    }

    private Task AnimateAsync(string name, Action<double> step, TimeSpan duration, Easing easing)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parts.Owner.Animate(name, step, FrameRate, ToMs(duration), easing, (_, _) => done.TrySetResult());
        return done.Task;
    }

    private static Task FadeAsync(VisualElement view, double opacity, TimeSpan duration, Easing easing)
    {
        if (duration <= TimeSpan.Zero)
        {
            view.Opacity = opacity;
            return Task.CompletedTask;
        }

        return view.FadeTo(opacity, ToMs(duration), easing);
    }

    private static Task ScaleAsync(VisualElement view, double scale, TimeSpan duration, Easing easing)
    {
        if (duration <= TimeSpan.Zero)
        {
            view.Scale = scale;
            return Task.CompletedTask;
        }

        return view.ScaleTo(scale, ToMs(duration), easing);
    }

    private static Task TranslateYAsync(VisualElement view, double y, TimeSpan duration, Easing easing)
    {
        if (duration <= TimeSpan.Zero)
        {
            view.TranslationY = y;
            return Task.CompletedTask;
        }

        return view.TranslateTo(view.TranslationX, y, ToMs(duration), easing);
    }

    private static uint ToMs(TimeSpan duration) => (uint)Math.Max(1, Math.Round(duration.TotalMilliseconds));
}
