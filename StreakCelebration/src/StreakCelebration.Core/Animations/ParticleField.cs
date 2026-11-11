namespace StreakCelebration.Animations;

/// <summary>
/// Confetti simulation. Positions are in device-independent units relative to the burst origin.
/// The particle buffer is allocated once and reused, and <see cref="Update"/> allocates nothing,
/// so it can run on every animation frame.
/// </summary>
public sealed class ParticleField
{
    private const float Gravity = 900f;
    private const float Drag = 0.9f;

    private Particle[] _particles = [];

    /// <summary>Number of particles in the current burst.</summary>
    public int Count { get; private set; }

    /// <summary>Seconds since the burst started.</summary>
    public float Elapsed { get; private set; }

    /// <summary><see langword="true"/> while at least one particle is visible.</summary>
    public bool IsAlive { get; private set; }

    /// <summary>Current particles; only the first <see cref="Count"/> entries are valid.</summary>
    public ReadOnlySpan<Particle> Particles => _particles.AsSpan(0, Count);

    /// <summary>Starts a new burst of <paramref name="count"/> particles that live up to <paramref name="lifetime"/>.</summary>
    /// <param name="count">Number of particles (0 clears the field).</param>
    /// <param name="lifetime">Longest particle lifetime.</param>
    /// <param name="spread">Launch speed multiplier, e.g. scaled with the icon size.</param>
    /// <param name="colorCount">Number of colors the renderer cycles through.</param>
    /// <param name="seed">Random seed; fixed seeds make bursts reproducible in tests.</param>
    public void Burst(int count, TimeSpan lifetime, float spread, int colorCount, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(colorCount, 1);

        if (_particles.Length < count) _particles = new Particle[count];
        Count = count;
        Elapsed = 0;
        IsAlive = count > 0;

        var random = new Random(seed);
        float maxLife = (float)lifetime.TotalSeconds;
        for (int i = 0; i < count; i++)
        {
            // Mostly upward fan (-170°..-10°), so confetti rises from the icon and falls back down.
            float angle = (float)((-170 + (random.NextDouble() * 160)) * Math.PI / 180);
            float speed = (float)(260 + (random.NextDouble() * 320)) * spread;
            _particles[i] = new Particle
            {
                VelocityX = MathF.Cos(angle) * speed,
                VelocityY = MathF.Sin(angle) * speed,
                Spin = (float)((random.NextDouble() - 0.5) * 720),
                Size = (float)(5 + (random.NextDouble() * 6)) * MathF.Max(0.75f, spread),
                Life = maxLife * (float)(0.65 + (random.NextDouble() * 0.35)),
                ColorIndex = i % colorCount,
                Alpha = 1,
            };
        }
    }

    /// <summary>Advances all particles to <paramref name="elapsedSeconds"/> after the burst. Allocation-free.</summary>
    public void Update(float elapsedSeconds)
    {
        Elapsed = elapsedSeconds;
        bool alive = false;
        float t = elapsedSeconds;
        // Closed-form motion with linear drag approximation: deterministic for any frame timing.
        float dragFactor = MathF.Pow(Drag, t);
        for (int i = 0; i < Count; i++)
        {
            ref Particle p = ref _particles[i];
            float age = MathF.Min(t, p.Life);
            float progress = p.Life <= 0 ? 1 : age / p.Life;
            p.X = p.VelocityX * age * dragFactor;
            p.Y = (p.VelocityY * age * dragFactor) + (0.5f * Gravity * age * age);
            p.Rotation = p.Spin * age;
            p.Alpha = t >= p.Life ? 0 : 1 - (progress * progress);
            alive |= p.Alpha > 0;
        }

        IsAlive = alive;
    }

    /// <summary>Removes all particles (keeps the buffer for the next burst).</summary>
    public void Clear()
    {
        Count = 0;
        Elapsed = 0;
        IsAlive = false;
    }
}

/// <summary>One confetti piece.</summary>
public struct Particle
{
    /// <summary>Horizontal offset from the origin.</summary>
    public float X;

    /// <summary>Vertical offset from the origin (positive is down).</summary>
    public float Y;

    /// <summary>Launch velocity, horizontal.</summary>
    public float VelocityX;

    /// <summary>Launch velocity, vertical.</summary>
    public float VelocityY;

    /// <summary>Rotation in degrees.</summary>
    public float Rotation;

    /// <summary>Degrees per second.</summary>
    public float Spin;

    /// <summary>Edge length.</summary>
    public float Size;

    /// <summary>Lifetime in seconds.</summary>
    public float Life;

    /// <summary>Opacity 0..1.</summary>
    public float Alpha;

    /// <summary>Index into the renderer's colors.</summary>
    public int ColorIndex;
}
