using Microsoft.Extensions.DependencyInjection;
using StreakCelebration.Animations;
using StreakCelebration.Models;
using StreakCelebration.Services;

namespace StreakCelebration.Tests;

public sealed class OptionsValidationTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(new StreakCelebrationOptions().Validate());
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        yield return Case("BackgroundOpacity", o => o.BackgroundOpacity = 1.5);
        yield return Case("BackgroundOpacity", o => o.BackgroundOpacity = double.NaN);
        yield return Case("Intensity", o => o.Intensity = -0.1);
        yield return Case("Intensity", o => o.Intensity = 2.5);
        yield return Case("AnimationDuration", o => o.AnimationDuration = TimeSpan.FromMilliseconds(10));
        yield return Case("DisplayDuration", o => o.DisplayDuration = TimeSpan.FromMinutes(5));
        yield return Case("CountFontSize", o => o.CountFontSize = 0);
        yield return Case("TitleFontSize", o => o.TitleFontSize = -1);
        yield return Case("IconSize", o => o.IconSize = 0);
        yield return Case("FontScale", o => o.FontScale = 10);
        yield return Case("EnableSound requires SoundFile", o => o.EnableSound = true);
        yield return Case("MaxQueueLength", o => o.MaxQueueLength = 0);
        yield return Case("QueuedLifetime", o => o.QueuedLifetime = TimeSpan.Zero);
        yield return Case("Position", o => o.Position = (CelebrationPosition)42);
        yield return Case("LightPalette.PrimaryColor", o => o.LightPalette = o.LightPalette with { PrimaryColor = "orange" });
        yield return Case("DarkPalette.BackdropColor", o => o.DarkPalette = o.DarkPalette with { BackdropColor = "#12345" });
        yield return Case("DarkPalette is required", o => o.DarkPalette = null!);

        static object[] Case(string expected, Action<StreakCelebrationOptions> change) => [expected, change];
    }

    [Theory, MemberData(nameof(InvalidOptions))]
    public void Invalid_values_are_reported(string expectedMessagePart, Action<StreakCelebrationOptions> change)
    {
        var options = new StreakCelebrationOptions();
        change(options);

        IReadOnlyList<string> errors = options.Validate();

        Assert.Contains(errors, e => e.Contains(expectedMessagePart, StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(options.ThrowIfInvalid);
    }

    [Fact]
    public void All_problems_are_reported_at_once()
    {
        var options = new StreakCelebrationOptions { BackgroundOpacity = 2, Intensity = 9, MaxQueueLength = 0 };

        var ex = Assert.Throws<ArgumentException>(options.ThrowIfInvalid);

        Assert.Contains("BackgroundOpacity", ex.Message);
        Assert.Contains("Intensity", ex.Message);
        Assert.Contains("MaxQueueLength", ex.Message);
    }

    [Theory]
    [InlineData("#FFF", 255, 255, 255, 255)]
    [InlineData("#F2C14E", 255, 242, 193, 78)]
    [InlineData("#80000000", 128, 0, 0, 0)]
    public void Hex_colors_are_parsed(string hex, byte a, byte r, byte g, byte b)
    {
        Assert.True(CelebrationPalette.TryParseColor(hex, out var argb));
        Assert.Equal((a, r, g, b), argb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("F2C14E")]
    [InlineData("#F2C1")]
    [InlineData("# FFFFF")]
    [InlineData("#GGGGGG")]
    public void Malformed_colors_are_rejected(string hex)
    {
        Assert.False(CelebrationPalette.TryParseColor(hex, out _));
    }

    [Fact]
    public void Service_rejects_invalid_options_and_snapshots_valid_ones()
    {
        Assert.Throws<ArgumentException>(() => new StreakCelebrationService(new StreakCelebrationOptions { Intensity = 5 }));

        var options = new StreakCelebrationOptions { DisplayDuration = TimeSpan.FromSeconds(3) };
        using var service = new StreakCelebrationService(options);
        options.DisplayDuration = TimeSpan.FromSeconds(9);

        Assert.Equal(TimeSpan.FromSeconds(3), service.Options.DisplayDuration);
    }

    [Fact]
    public void DI_registration_validates_and_shares_one_service_instance()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddStreakCelebrations(o => o.MaxQueueLength = 0));

        using ServiceProvider provider = new ServiceCollection()
            .AddStreakCelebrations(o => o.Position = CelebrationPosition.Top)
            .BuildServiceProvider();

        var service = provider.GetRequiredService<IStreakCelebrationService>();
        Assert.Same(service, provider.GetRequiredService<IStreakCelebrationHostRegistry>());
        Assert.Equal(CelebrationPosition.Top, ((StreakCelebrationService)service).Options.Position);
    }

    [Fact]
    public void DI_registration_keeps_platform_services_registered_before()
    {
        var sound = new RecordingSoundPlayer();
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<ICelebrationSoundPlayer>(sound)
            .AddStreakCelebrations()
            .BuildServiceProvider();

        Assert.Same(sound, provider.GetRequiredService<ICelebrationSoundPlayer>());
    }

    [Fact]
    public void Celebration_data_is_validated()
    {
        Assert.Empty(new StreakCelebrationData { Title = "10 STREAK!", Count = 10 }.Validate());
        Assert.Equal(3, new StreakCelebrationData { Title = " ", Count = -1, Level = (CelebrationLevel)9 }.Validate().Count);
    }

    [Fact]
    public void Announcement_combines_title_and_message_unless_overridden()
    {
        Assert.Equal("10 STREAK! UNSTOPPABLE!", new StreakCelebrationData { Title = "10 STREAK!", Message = "UNSTOPPABLE!" }.Announcement);
        Assert.Equal("10 STREAK!", new StreakCelebrationData { Title = "10 STREAK!" }.Announcement);
        Assert.Equal("custom", new StreakCelebrationData { Title = "t", AccessibilityText = "custom" }.Announcement);
    }
}

public sealed class TimelineTests
{
    private static readonly StreakCelebrationOptions Options = new();

    private static CelebrationTimeline Plan(CelebrationLevel level, int count = 10, StreakCelebrationOptions? options = null, bool reduced = false)
        => CelebrationTimeline.Create(new StreakCelebrationData { Title = "t", Count = count, Level = level }, options ?? Options, reduced);

    [Fact]
    public void Levels_escalate_from_normal_to_record()
    {
        CelebrationTimeline normal = Plan(CelebrationLevel.Normal);
        CelebrationTimeline milestone = Plan(CelebrationLevel.Milestone);
        CelebrationTimeline record = Plan(CelebrationLevel.Record);

        Assert.True(normal.IconOvershoot < milestone.IconOvershoot && milestone.IconOvershoot < record.IconOvershoot);
        Assert.True(normal.ParticleCount < milestone.ParticleCount && milestone.ParticleCount < record.ParticleCount);
        Assert.True(normal.GlowStrength < milestone.GlowStrength && milestone.GlowStrength <= record.GlowStrength);
        Assert.True(normal.Hold < milestone.Hold && milestone.Hold < record.Hold);
        Assert.Equal(0, normal.ParticleCount);
        Assert.Equal(Options.BackgroundOpacity / 2, normal.BackdropOpacity, 3);
    }

    [Fact]
    public void The_level_comes_from_the_caller_not_from_the_count()
    {
        Assert.Equal(CelebrationLevel.Normal, Plan(CelebrationLevel.Normal, count: 1000).Level);
        Assert.Equal(CelebrationLevel.Record, Plan(CelebrationLevel.Record, count: 1).Level);
    }

    [Fact]
    public void Reduced_motion_replaces_motion_with_short_fades_but_keeps_reading_time()
    {
        CelebrationTimeline plan = Plan(CelebrationLevel.Record, reduced: true);

        Assert.True(plan.ReducedMotion);
        Assert.Equal(TimeSpan.Zero, plan.IconEntrance);
        Assert.Equal(TimeSpan.Zero, plan.CountUp);
        Assert.Equal(1.0, plan.IconOvershoot);
        Assert.Equal(0, plan.ParticleCount);
        Assert.Equal(0, plan.GlowPulses);
        Assert.Equal(plan.CountTo, plan.CountFrom);
        Assert.True(plan.FadeIn <= TimeSpan.FromMilliseconds(200));
        Assert.Equal(Plan(CelebrationLevel.Record).Hold, plan.Hold);
    }

    [Fact]
    public void Particles_can_be_disabled_and_scale_with_intensity()
    {
        Assert.Equal(0, Plan(CelebrationLevel.Record, options: new StreakCelebrationOptions { EnableParticles = false }).ParticleCount);
        Assert.Equal(0, Plan(CelebrationLevel.Record, options: new StreakCelebrationOptions { Intensity = 0 }).ParticleCount);

        int normal = Plan(CelebrationLevel.Record).ParticleCount;
        int intense = Plan(CelebrationLevel.Record, options: new StreakCelebrationOptions { Intensity = 2 }).ParticleCount;
        Assert.True(intense > normal);
        Assert.True(intense <= CelebrationTimeline.MaxParticles);
    }

    [Fact]
    public void Durations_scale_with_the_configured_animation_and_display_durations()
    {
        var slow = new StreakCelebrationOptions { AnimationDuration = TimeSpan.FromMilliseconds(900), DisplayDuration = TimeSpan.FromSeconds(4) };

        CelebrationTimeline plan = Plan(CelebrationLevel.Milestone, options: slow);

        Assert.Equal(TimeSpan.FromMilliseconds(450), plan.FadeIn);
        Assert.Equal(TimeSpan.FromSeconds(4), plan.Hold);
        Assert.Equal(plan.FadeIn + plan.IconEntrance + plan.CountUp + plan.TextReveal + plan.Hold + plan.FadeOut, plan.TotalDuration);
        Assert.True(plan.DismissFadeOut < plan.FadeOut);
    }

    [Fact]
    public void Count_animation_starts_below_the_final_count_but_never_below_zero()
    {
        Assert.Equal(9, Plan(CelebrationLevel.Normal, count: 10).CountFrom);
        Assert.Equal(0, Plan(CelebrationLevel.Record, count: 3).CountFrom);
        Assert.Equal(TimeSpan.Zero, Plan(CelebrationLevel.Milestone, count: 0).CountUp);
    }
}

public sealed class ParticleFieldTests
{
    [Fact]
    public void Particles_fly_fade_and_die()
    {
        var field = new ParticleField();
        field.Burst(20, TimeSpan.FromSeconds(1.5), spread: 1, colorCount: 3, seed: 7);

        field.Update(0.3f);
        Assert.True(field.IsAlive);
        foreach (Particle p in field.Particles)
        {
            Assert.InRange(p.Alpha, 0.01f, 1f);
            Assert.NotEqual(0f, p.X + p.Y);
        }

        field.Update(2f);
        Assert.False(field.IsAlive);
        foreach (Particle p in field.Particles) Assert.Equal(0f, p.Alpha);
    }

    [Fact]
    public void Same_seed_gives_the_same_burst()
    {
        var a = new ParticleField();
        var b = new ParticleField();
        a.Burst(10, TimeSpan.FromSeconds(1), 1, 3, seed: 42);
        b.Burst(10, TimeSpan.FromSeconds(1), 1, 3, seed: 42);
        a.Update(0.5f);
        b.Update(0.5f);

        Assert.Equal(a.Particles.ToArray(), b.Particles.ToArray());
    }

    [Fact]
    public void Clear_releases_the_burst_and_the_buffer_is_reused()
    {
        var field = new ParticleField();
        field.Burst(30, TimeSpan.FromSeconds(1), 1, 3, seed: 1);
        field.Clear();

        Assert.Equal(0, field.Count);
        Assert.False(field.IsAlive);
        Assert.True(field.Particles.IsEmpty);

        field.Burst(10, TimeSpan.FromSeconds(1), 1, 3, seed: 2);
        Assert.Equal(10, field.Count);
    }

    [Fact]
    public void Update_does_not_allocate()
    {
        var field = new ParticleField();
        field.Burst(60, TimeSpan.FromSeconds(2), 1, 3, seed: 3);
        field.Update(0.01f); // warm-up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 1; frame <= 120; frame++) field.Update(frame / 60f);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
