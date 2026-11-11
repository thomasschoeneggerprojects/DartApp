namespace SportTA.Streaks.Evaluation;

/// <summary>
/// Maps instants to calendar periods in a given time zone. Periods are integer indices (local day numbers or week
/// numbers), so gaps of any length are computed arithmetically and DST transitions never affect day boundaries.
/// </summary>
internal static class StreakPeriods
{
    public static int GetDayNumber(DateTimeOffset instant, TimeZoneInfo timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime).DayNumber;

    public static long GetPeriod(StreakDefinition definition, DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        int day = GetDayNumber(instant, timeZone);
        return definition.Period switch
        {
            StreakPeriod.Daily => day,
            StreakPeriod.Weekly => FloorDivide(day - WeekShift(definition.WeekStartDay), 7),
            _ => throw new InvalidOperationException("Per-activity streaks have no calendar periods."),
        };
    }

    public static (DateTimeOffset Start, DateTimeOffset End) GetBounds(StreakDefinition definition, long period, TimeZoneInfo timeZone)
    {
        long startDay = definition.Period == StreakPeriod.Daily
            ? period
            : (period * 7) + WeekShift(definition.WeekStartDay);
        long length = definition.Period == StreakPeriod.Daily ? 1 : 7;
        return (StartOfDay((int)startDay, timeZone), StartOfDay((int)(startDay + length), timeZone));
    }

    /// <summary>First instant of a local calendar day, handling DST gaps and ambiguous midnights.</summary>
    public static DateTimeOffset StartOfDay(int dayNumber, TimeZoneInfo timeZone)
    {
        DateTime local = DateOnly.FromDayNumber(dayNumber).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Some zones skip midnight on DST start; the day then begins at the first valid local time.
        for (int i = 0; i < 96 && timeZone.IsInvalidTime(local); i++)
        {
            local = local.AddMinutes(15);
        }

        TimeSpan offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    // Day number 0 (0001-01-01) is a Monday.
    private static int WeekShift(DayOfWeek weekStart) => ((int)weekStart + 6) % 7;

    private static long FloorDivide(long value, long divisor)
        => value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}
