namespace SportTA.Streaks.Tests.Infrastructure;

/// <summary>Custom time zones so results never depend on the operating system's time zone database.</summary>
public static class TestTimeZones
{
    /// <summary>Central European time: UTC+1, DST UTC+2 from the last Sunday in March 02:00 to the last Sunday in October 03:00.</summary>
    public static TimeZoneInfo Vienna { get; } = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Vienna",
        TimeSpan.FromHours(1),
        "Test Vienna",
        "CET",
        "CEST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    public static TimeZoneInfo UtcMinus8 { get; } = TimeZoneInfo.CreateCustomTimeZone("Test/UTC-8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");

    /// <summary>Creates an instant from a local wall-clock time in <paramref name="timeZone"/>.</summary>
    public static DateTimeOffset At(TimeZoneInfo timeZone, int year, int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }
}
