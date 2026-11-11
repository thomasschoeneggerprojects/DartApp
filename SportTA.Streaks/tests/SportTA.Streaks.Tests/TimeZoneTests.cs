using System.Globalization;
using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class TimeZoneTests
{
    private static readonly StreakDefinition Daily = new() { Id = "daily" };
    private static readonly TimeZoneInfo Vienna = TestTimeZones.Vienna;

    [Fact]
    public async Task DayBoundaries_FollowPlayerTimeZone_NotUtc()
    {
        DateTimeOffset beforeMidnight = TestTimeZones.At(Vienna, 2026, 11, 2, 23, 30); // 22:30 UTC
        DateTimeOffset afterMidnight = TestTimeZones.At(Vienna, 2026, 11, 3, 0, 30);   // 23:30 UTC, same UTC day

        var vienna = new StreakTestContext(beforeMidnight, [Daily], o => o.DefaultTimeZone = Vienna);
        await vienna.RecordAtAsync(beforeMidnight, "daily");
        StreakResult viennaResult = await vienna.RecordAtAsync(afterMidnight, "daily");

        var utc = new StreakTestContext(beforeMidnight, [Daily]);
        await utc.RecordAtAsync(beforeMidnight, "daily");
        StreakResult utcResult = await utc.RecordAtAsync(afterMidnight, "daily");

        Assert.Equal(2, viennaResult.Progress.CurrentCount);
        Assert.Equal(StreakResultCode.AlreadyCountedForPeriod, utcResult.Code);
    }

    [Fact]
    public async Task PerPlayerTimeZones_AreIndependent()
    {
        var viennaPlayer = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var utcPlayer = Guid.Parse("44444444-4444-4444-4444-444444444444");
        DateTimeOffset first = Utc(2026, 11, 2, 22, 30);
        DateTimeOffset second = Utc(2026, 11, 2, 23, 30);
        var ctx = new StreakTestContext(first, [Daily], o => o.PlayerTimeZoneResolver = id => id == viennaPlayer ? Vienna : TimeZoneInfo.Utc);

        foreach (Guid player in new[] { viennaPlayer, utcPlayer })
        {
            await ctx.RecordAtAsync(first, "daily", playerId: player);
            await ctx.RecordAtAsync(second, "daily", playerId: player);
        }

        Assert.Equal(2, (await ctx.Service.GetProgressAsync(viennaPlayer, "daily")).CurrentCount);
        Assert.Equal(1, (await ctx.Service.GetProgressAsync(utcPlayer, "daily")).CurrentCount);
    }

    [Fact]
    public async Task TimeZoneChangeWestwards_NeitherBreaksNorDoubleCounts()
    {
        TimeZoneInfo current = Vienna;
        var ctx = new StreakTestContext(Utc(2026, 11, 2, 7), [Daily], o => o.PlayerTimeZoneResolver = _ => current);
        await ctx.RecordAtAsync(Utc(2026, 11, 2, 7), "daily"); // Nov 2, 08:00 Vienna

        current = TestTimeZones.UtcMinus8;
        StreakResult sameLocalDay = await ctx.RecordAtAsync(Utc(2026, 11, 3, 3), "daily"); // Nov 2, 19:00 UTC-8
        StreakResult nextLocalDay = await ctx.RecordAtAsync(Utc(2026, 11, 4, 3), "daily"); // Nov 3, 19:00 UTC-8

        Assert.Equal(StreakResultCode.AlreadyCountedForPeriod, sameLocalDay.Code);
        Assert.Equal(StreakResultCode.Increased, nextLocalDay.Code);
        Assert.Equal(2, nextLocalDay.Progress.CurrentCount);
    }

    [Fact]
    public async Task DaylightSavingStart_ShortDayIsOneDay()
    {
        var ctx = new StreakTestContext(Utc(2026, 3, 28), [Daily], o => o.DefaultTimeZone = Vienna);

        await ctx.RecordAtAsync(TestTimeZones.At(Vienna, 2026, 3, 28, 23, 30), "daily");
        await ctx.RecordAtAsync(TestTimeZones.At(Vienna, 2026, 3, 29, 23, 30), "daily");
        StreakResult third = await ctx.RecordAtAsync(TestTimeZones.At(Vienna, 2026, 3, 30, 0, 30), "daily");
        StreakProgress onDstDay = await ctx.ProgressAtAsync(TestTimeZones.At(Vienna, 2026, 3, 29, 12), "daily");

        Assert.Equal(3, third.Progress.CurrentCount);
        Assert.Equal(Utc(2026, 3, 28, 23), onDstDay.CurrentPeriodStart);
        Assert.Equal(TimeSpan.FromHours(23), onDstDay.CurrentPeriodEnd!.Value - onDstDay.CurrentPeriodStart!.Value);
    }

    [Fact]
    public async Task DaylightSavingEnd_RepeatedHourStaysOnSameDay()
    {
        var ctx = new StreakTestContext(Utc(2026, 10, 24), [Daily], o => o.DefaultTimeZone = Vienna);

        await ctx.RecordAtAsync(TestTimeZones.At(Vienna, 2026, 10, 24, 23), "daily");
        StreakResult firstTwoThirty = await ctx.RecordAtAsync(Utc(2026, 10, 25, 0, 30), "daily");  // 02:30 CEST
        StreakResult secondTwoThirty = await ctx.RecordAtAsync(Utc(2026, 10, 25, 1, 30), "daily"); // 02:30 CET
        StreakResult nextDay = await ctx.RecordAtAsync(TestTimeZones.At(Vienna, 2026, 10, 26, 0, 10), "daily");
        StreakProgress onDstDay = await ctx.ProgressAtAsync(TestTimeZones.At(Vienna, 2026, 10, 25, 12), "daily");

        Assert.Equal(StreakResultCode.Increased, firstTwoThirty.Code);
        Assert.Equal(StreakResultCode.AlreadyCountedForPeriod, secondTwoThirty.Code);
        Assert.Equal(3, nextDay.Progress.CurrentCount);
        Assert.Equal(TimeSpan.FromHours(25), onDstDay.CurrentPeriodEnd!.Value - onDstDay.CurrentPeriodStart!.Value);
    }

    [Theory]
    [InlineData("2026-12-31,2027-01-01", 2)]
    [InlineData("2027-01-31,2027-02-01", 2)]
    [InlineData("2027-02-28,2027-03-01", 2)]
    [InlineData("2028-02-28,2028-02-29,2028-03-01", 3)]
    [InlineData("2028-02-28,2028-03-01", 1)]
    public async Task MonthAndYearBoundaries(string dates, int expectedCount)
    {
        DateOnly[] days = dates.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        var ctx = new StreakTestContext(Utc(days[0].Year, days[0].Month, days[0].Day), [Daily]);

        StreakResult? last = null;
        foreach (DateOnly day in days)
        {
            last = await ctx.RecordAtAsync(Utc(day.Year, day.Month, day.Day, 20), "daily");
        }

        Assert.Equal(expectedCount, last!.Progress.CurrentCount);
    }
}
