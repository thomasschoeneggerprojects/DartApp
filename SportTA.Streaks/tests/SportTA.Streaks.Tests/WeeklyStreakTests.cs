using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

/// <remarks>2026-11-02 is a Monday.</remarks>
public sealed class WeeklyStreakTests
{
    private static readonly StreakDefinition ThreePerWeek = new()
    {
        Id = "weekly",
        Period = StreakPeriod.Weekly,
        RequiredActivities = 3,
    };

    [Fact]
    public async Task ThreeActivities_QualifyWeek()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [ThreePerWeek]);

        StreakResult monday = await ctx.RecordAtAsync(Utc(2026, 11, 2), "weekly");
        StreakResult wednesday = await ctx.RecordAtAsync(Utc(2026, 11, 4), "weekly");
        StreakResult friday = await ctx.RecordAtAsync(Utc(2026, 11, 6), "weekly");
        StreakResult saturday = await ctx.RecordAtAsync(Utc(2026, 11, 7), "weekly");

        Assert.Equal(StreakResultCode.PeriodProgress, monday.Code);
        Assert.Equal(StreakResultCode.PeriodProgress, wednesday.Code);
        Assert.Equal(StreakResultCode.Started, friday.Code);
        Assert.Equal(StreakResultCode.AlreadyCountedForPeriod, saturday.Code);
        Assert.Equal(1, saturday.Progress.CurrentCount);
        Assert.True(saturday.Progress.IsCurrentPeriodComplete);
    }

    [Fact]
    public async Task ConsecutiveWeeks_IncreaseStreak()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [ThreePerWeek]);
        foreach (int day in new[] { 2, 3, 4, 9, 11, 13 })
        {
            await ctx.RecordAtAsync(Utc(2026, 11, day), "weekly");
        }

        StreakProgress progress = await ctx.ProgressAtAsync(Utc(2026, 11, 14), "weekly");

        Assert.Equal(2, progress.CurrentCount);
        Assert.Equal(StreakStatus.Active, progress.Status);
    }

    [Fact]
    public async Task IncompleteWeek_IsNotMissedUntilItEnds()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [ThreePerWeek]);
        foreach (int day in new[] { 2, 4, 6, 9, 10 })
        {
            await ctx.RecordAtAsync(Utc(2026, 11, day), "weekly");
        }

        StreakResult sunday = await ctx.EvaluateAtAsync(Utc(2026, 11, 15, 23, 59), "weekly");
        StreakResult monday = await ctx.EvaluateAtAsync(Utc(2026, 11, 16, 0, 1), "weekly");

        Assert.Equal(StreakResultCode.NoChange, sunday.Code);
        Assert.Equal(StreakStatus.Active, sunday.Progress.Status);
        Assert.True(sunday.Progress.IsAtRisk);
        Assert.Equal(2, sunday.Progress.ActivitiesInCurrentPeriod);
        Assert.Equal(StreakResultCode.Broken, monday.Code);
        Assert.Equal(1, monday.Progress.LongestCount);
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, StreakResultCode.AlreadyCountedForPeriod, 1)]
    [InlineData(DayOfWeek.Sunday, StreakResultCode.Increased, 2)]
    public async Task WeekStartDay_DeterminesWeekBoundaries(DayOfWeek weekStart, StreakResultCode expectedCode, int expectedCount)
    {
        var definition = new StreakDefinition { Id = "weekly", Period = StreakPeriod.Weekly, WeekStartDay = weekStart };
        var ctx = new StreakTestContext(Utc(2026, 11, 7), [definition]);

        await ctx.RecordAtAsync(Utc(2026, 11, 7), "weekly"); // Saturday
        StreakResult sunday = await ctx.RecordAtAsync(Utc(2026, 11, 8), "weekly");

        Assert.Equal(expectedCode, sunday.Code);
        Assert.Equal(expectedCount, sunday.Progress.CurrentCount);
    }

    [Fact]
    public async Task CountDistinctDays_IgnoresRepeatedActivitiesOnSameDay()
    {
        var definition = new StreakDefinition
        {
            Id = "active-days",
            Period = StreakPeriod.Weekly,
            RequiredActivities = 3,
            CountDistinctDays = true,
        };
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [definition]);

        StreakResult first = await ctx.RecordAtAsync(Utc(2026, 11, 2, 8), "active-days");
        StreakResult sameDay = await ctx.RecordAtAsync(Utc(2026, 11, 2, 18), "active-days");
        StreakResult secondDay = await ctx.RecordAtAsync(Utc(2026, 11, 3), "active-days");
        StreakResult thirdDay = await ctx.RecordAtAsync(Utc(2026, 11, 5), "active-days");

        Assert.Equal(StreakResultCode.PeriodProgress, first.Code);
        Assert.Equal(StreakResultCode.AlreadyCountedForPeriod, sameDay.Code);
        Assert.Equal(1, sameDay.Progress.ActivitiesInCurrentPeriod);
        Assert.Equal(StreakResultCode.PeriodProgress, secondDay.Code);
        Assert.Equal(StreakResultCode.Started, thirdDay.Code);
        Assert.Equal(4, thirdDay.Progress.TotalQualifyingActivities);
    }

    [Fact]
    public async Task PeriodBounds_UsePlayerTimeZone()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 4), [ThreePerWeek], o => o.DefaultTimeZone = TestTimeZones.Vienna);

        StreakProgress progress = await ctx.Service.GetProgressAsync(ctx.PlayerId, "weekly");

        Assert.Equal(Utc(2026, 11, 1, 23), progress.CurrentPeriodStart);
        Assert.Equal(Utc(2026, 11, 8, 23), progress.CurrentPeriodEnd);
    }

    [Fact]
    public async Task WeekSpanningYearBoundary_IsOneWeek()
    {
        var definition = new StreakDefinition { Id = "weekly", Period = StreakPeriod.Weekly, RequiredActivities = 2 };
        var ctx = new StreakTestContext(Utc(2026, 12, 31), [definition]);

        StreakResult thursday = await ctx.RecordAtAsync(Utc(2026, 12, 31), "weekly");
        StreakResult saturday = await ctx.RecordAtAsync(Utc(2027, 1, 2), "weekly");

        Assert.Equal(StreakResultCode.PeriodProgress, thursday.Code);
        Assert.Equal(StreakResultCode.Started, saturday.Code);
    }

    [Fact]
    public void CountDistinctDays_OnDailyStreak_IsRejected()
    {
        var registry = new StreakRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new StreakDefinition { Id = "bad", CountDistinctDays = true }));
    }
}
