using StreakCelebration.Animations;

namespace StreakCelebration.Animations;

/// <summary>
/// Draws the built-in vector flame and its glow. Vector drawing stays sharp on every screen density and needs no image
/// asset. Paints are rebuilt only when colors change, never per frame.
/// </summary>
internal sealed class FlameIconDrawable : IDrawable
{
    // Flame outline in a 100 x 120 design box.
    private readonly PathF _outer = Closed(new PathF()
        .MoveTo(50, 0)
        .CurveTo(56, 20, 80, 36, 86, 62)
        .CurveTo(92, 92, 74, 120, 50, 120)
        .CurveTo(26, 120, 8, 102, 12, 76)
        .CurveTo(14, 62, 22, 52, 30, 44)
        .CurveTo(30, 58, 36, 66, 44, 68)
        .CurveTo(40, 46, 42, 22, 50, 0));

    private readonly PathF _inner = Closed(new PathF()
        .MoveTo(52, 50)
        .CurveTo(58, 66, 72, 76, 72, 94)
        .CurveTo(72, 110, 62, 118, 50, 118)
        .CurveTo(38, 118, 28, 110, 28, 96)
        .CurveTo(28, 84, 36, 78, 40, 72)
        .CurveTo(42, 80, 46, 84, 52, 84)
        .CurveTo(49, 72, 47, 62, 52, 50));

    private Paint _flamePaint = new SolidPaint(Colors.Orange);
    private Paint _corePaint = new SolidPaint(Colors.Yellow);
    private Paint _glowPaint = new SolidPaint(Colors.Transparent);

    /// <summary>Flame height in device-independent units.</summary>
    public float FlameSize { get; set; } = 112;

    /// <summary>Draws the flame; <see langword="false"/> when an image icon is shown instead.</summary>
    public bool DrawFlame { get; set; } = true;

    /// <summary>Glow opacity 0..1.</summary>
    public float GlowAlpha { get; set; }

    /// <summary>Glow radius multiplier.</summary>
    public float GlowScale { get; set; } = 1;

    private static PathF Closed(PathF path)
    {
        path.Close();
        return path;
    }

    public void SetColors(Color primary, Color secondary)
    {
        _flamePaint = new LinearGradientPaint
        {
            StartColor = primary,
            EndColor = secondary,
            StartPoint = new Point(0.5, 1),
            EndPoint = new Point(0.5, 0),
        };
        _corePaint = new LinearGradientPaint
        {
            StartColor = secondary,
            EndColor = Color.FromRgba(1, 0.97, 0.82, 1),
            StartPoint = new Point(0.5, 1),
            EndPoint = new Point(0.5, 0),
        };
        _glowPaint = new RadialGradientPaint
        {
            Center = new Point(0.5, 0.5),
            Radius = 0.5,
            GradientStops =
            [
                new PaintGradientStop(0f, primary.WithAlpha(0.85f)),
                new PaintGradientStop(0.55f, primary.WithAlpha(0.35f)),
                new PaintGradientStop(1f, primary.WithAlpha(0f)),
            ],
        };
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float cx = dirtyRect.Center.X;
        float cy = dirtyRect.Center.Y;

        if (GlowAlpha > 0.001f)
        {
            float radius = FlameSize * 0.8f * GlowScale;
            var glowRect = new RectF(cx - radius, cy - radius, radius * 2, radius * 2);
            canvas.SaveState();
            canvas.Alpha = GlowAlpha;
            canvas.SetFillPaint(_glowPaint, glowRect);
            canvas.FillEllipse(glowRect);
            canvas.RestoreState();
        }

        if (!DrawFlame) return;

        float scale = FlameSize / 120f;
        canvas.SaveState();
        canvas.Translate(cx - (50 * scale), cy - (60 * scale));
        canvas.Scale(scale, scale);
        canvas.SetFillPaint(_flamePaint, new RectF(0, 0, 100, 120));
        canvas.FillPath(_outer);
        canvas.SetFillPaint(_corePaint, new RectF(28, 50, 44, 68));
        canvas.FillPath(_inner);
        canvas.RestoreState();
    }
}

/// <summary>Renders a <see cref="ParticleField"/> as rotating confetti rectangles. Allocation-free per frame.</summary>
internal sealed class ParticleDrawable(ParticleField field) : IDrawable
{
    private readonly Color[] _colors = [Colors.Orange, Colors.Gold, Colors.White];

    /// <summary>Burst origin in the canvas' coordinates.</summary>
    public PointF Origin { get; set; }

    /// <summary>Number of colors particles cycle through.</summary>
    public int ColorCount => _colors.Length;

    public void SetColors(Color primary, Color secondary, Color accent)
    {
        _colors[0] = primary;
        _colors[1] = secondary;
        _colors[2] = accent;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        ReadOnlySpan<Particle> particles = field.Particles;
        if (particles.IsEmpty) return;

        for (int i = 0; i < particles.Length; i++)
        {
            ref readonly Particle p = ref particles[i];
            if (p.Alpha <= 0) continue;
            canvas.SaveState();
            canvas.Alpha = p.Alpha;
            canvas.FillColor = _colors[p.ColorIndex % _colors.Length];
            canvas.Translate(Origin.X + p.X, Origin.Y + p.Y);
            canvas.Rotate(p.Rotation);
            canvas.FillRectangle(-p.Size / 2, -p.Size * 0.3f, p.Size, p.Size * 0.6f);
            canvas.RestoreState();
        }
    }
}
