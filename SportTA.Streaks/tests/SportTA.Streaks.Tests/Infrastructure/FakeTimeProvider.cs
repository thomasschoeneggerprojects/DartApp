namespace SportTA.Streaks.Tests.Infrastructure;

/// <summary>Deterministic clock: tests never depend on the real current date.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private long _utcTicks;

    public FakeTimeProvider(DateTimeOffset start) => SetUtcNow(start);

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void SetUtcNow(DateTimeOffset value) => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);
}
