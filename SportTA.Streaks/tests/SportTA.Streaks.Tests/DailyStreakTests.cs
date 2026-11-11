using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class DailyStreakTests
{
    private static readonly StreakDefinition Daily = new() { Id = "daily", Period = StreakPeriod.Daily, Milestones = [3, 7] };

    private static StreakTestContext Create(params StreakDefinition[] definitions)
        => new(Utc(2026, 11, 2), definitions.Length == 0 ? [Daily] : definitions);

    [Fact]
    public async Task FirstActivity_StartsStreak()
    {
        var ctx = Create();

        StreakResult result = await ctx.RecordAtAsync(Utc(2026, 11, 2, 9), "daily");

        Assert.Equal(StreakResultCode.Started, result.Code);
        Assert.True(result.StateChanged);
        Assert.Equal(1, result.Progress.CurrentCount);
        Assert.Equal(1, result.Progress.LongestCount);
        Assert.Equal(StreakStatus.Active, result.Progress.Status);
        Assert.True(result.Progress.IsCurrentPeriodComplete);
        Assert.False(result.Progress.IsAtRisk);
        Assert.NotNull(result.Progress.StreakId);
        StreakEvent started = Assert.Single(result.Events);
        Assert.Equal(StreakEventType.Started, started.Type);
        Assert.Equal(1, started.CurrentCount);
    }

    [Fact]
    public async Task ConsecutiveDays_IncreaseStreak()
    {
        var ctx = Create();

        StreakResult? last = null;
        for (int day = 0; day < 5; day++)
        {
            last = await ctx.RecordAtAsync(Utc(2026, 11, 2 + day, 18), "daily");
        }

        Assert.Equal(StreakResultCode.Increased, last!.Code);
        Assert.Equal(5, last.Progress.CurrentCount);
        Assert.Equal(5, last.Progress.LongestCount);
        Assert.Equal(Utc(2026, 11, 2, 18), last.Progress.CurrentStreakStartedAt);
    }

    [Fact]
    public async Task MultipleActivitiesOnSameDay_CountOnce()
    {
        var ctx = Create();

        StreakResult first = await ctx.RecordAtAsync(Utc(2026, 11, 2, 0, 1), "daily");
        StreakResult second = await ctx.RecordAtAsync(Utc(2026, 11, 2, 12), "daily");
        StreakResult third = await ctx.RecordAtAsync(Utc(2026, 11, 2, 23, 59), "daily");

        Assert.Equal(StreakResultCode.Started, first.Code);
        Assert.Equal(StreakResultCode.AlreadyCountedForPeriod, second.Code);
        Assert.Equal(StreakResultCode.AlreadyCountedForPeriod, third.Code);
        Assert.Empty(second.Events);
        Assert.Equal(1, third.Progress.CurrentCount);
        Assert.Equal(3, third.Progress.TotalQualifyingActivities);
    }

    [Fact]
    public async Task MissedDay_BreaksStreakAndStartsNewRun()
    {
        var ctx = Create();
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily");
        await ctx.RecordAtAsync(Utc(2026, 11, 3), "daily");

        StreakResult result = await ctx.RecordAtAsync(Utc(2026, 11, 5), "daily");

        Assert.Equal(StreakResultCode.Started, result.Code);
        Assert.Equal(new[] { StreakEventType.Broken, StreakEventType.Started }, result.Events.Select(e => e.Type));
        Assert.Equal(2, result.Events[0].PreviousCount);
        Assert.Equal(1, result.Progress.CurrentCount);
        Assert.Equal(2, result.Progress.LongestCount);

        StreakStatistics stats = await ctx.Service.GetStatisticsAsync(ctx.PlayerId, "daily");
        Assert.Equal(1, stats.TimesBroken);
        Assert.Equal(3, stats.TotalQualifiedPeriods);
    }

    [Fact]
    public async Task Evaluate_AfterMissedDay_BreaksAndPreservesLongest()
    {
        var ctx = Create();
        for (int day = 2; day <= 4; day++)
        {
            await ctx.RecordAtAsync(Utc(2026, 11, day), "daily");
        }

        StreakResult result = await ctx.EvaluateAtAsync(Utc(2026, 11, 6, 10), "daily");

        Assert.Equal(StreakResultCode.Broken, result.Code);
        Assert.Equal(StreakStatus.Broken, result.Progress.Status);
        Assert.Equal(0, result.Progress.CurrentCount);
        Assert.Equal(3, result.Progress.LongestCount);
        Assert.Equal(3, result.Progress.RestorableCount);
        Assert.Equal(StreakEventType.Broken, Assert.Single(result.Events).Type);

        StreakResult again = await ctx.EvaluateAtAsync(Utc(2026, 11, 7, 10), "daily");
        Assert.Equal(StreakResultCode.NoChange, again.Code);
        Assert.Empty(again.Events);
    }

    [Fact]
    public async Task Evaluate_BeforeDayIsOver_DoesNotBreak_ButReportsRisk()
    {
        var ctx = Create();
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily");
        await ctx.RecordAtAsync(Utc(2026, 11, 3), "daily");

        StreakResult sameDay = await ctx.EvaluateAtAsync(Utc(2026, 11, 3, 23), "daily");
        StreakResult nextDay = await ctx.EvaluateAtAsync(Utc(2026, 11, 4, 23, 59), "daily");

        Assert.Equal(StreakResultCode.NoChange, sameDay.Code);
        Assert.Equal(StreakResultCode.NoChange, nextDay.Code);
        Assert.Equal(StreakStatus.Active, nextDay.Progress.Status);
        Assert.Equal(2, nextDay.Progress.CurrentCount);
        Assert.True(nextDay.Progress.IsAtRisk);
        Assert.False(nextDay.Progress.IsCurrentPeriodComplete);
    }

    [Fact]
    public async Task RequiredActivitiesPerDay_CountsProgressUntilMet()
    {
        var definition = new StreakDefinition { Id = "two-a-day", RequiredActivities = 2 };
        var ctx = Create(definition);

        StreakResult first = await ctx.RecordAtAsync(Utc(2026, 11, 2, 8), "two-a-day");
        StreakResult second = await ctx.RecordAtAsync(Utc(2026, 11, 2, 18), "two-a-day");

        Assert.Equal(StreakResultCode.PeriodProgress, first.Code);
        Assert.Equal(0, first.Progress.CurrentCount);
        Assert.Equal(1, first.Progress.ActivitiesInCurrentPeriod);
        Assert.Equal(StreakStatus.NotStarted, first.Progress.Status);
        Assert.Empty(first.Events);
        Assert.Equal(StreakResultCode.Started, second.Code);
        Assert.Equal(2, second.Progress.ActivitiesInCurrentPeriod);
    }

    [Fact]
    public async Task GetProgress_WithoutData_ReturnsNotStarted_AndStoresNothing()
    {
        var ctx = Create();

        StreakProgress progress = await ctx.Service.GetProgressAsync(ctx.PlayerId, "daily");

        Assert.Equal(StreakStatus.NotStarted, progress.Status);
        Assert.Null(progress.StreakId);
        Assert.Equal(3, progress.NextMilestone);
        Assert.Equal(0, ((InMemoryStreakRepository)ctx.Repository).Count);
    }

    [Fact]
    public async Task LongInactivity_IsEvaluatedArithmetically()
    {
        var ctx = Create();
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily");
        await ctx.Service.AddFreezesAsync(ctx.PlayerId, "daily", 1000);

        StreakResult result = await ctx.EvaluateAtAsync(Utc(2036, 11, 2), "daily");

        Assert.Equal(StreakResultCode.Broken, result.Code);
        Assert.Equal(1000, result.Progress.FreezesAvailable);
        Assert.Equal(1, result.Progress.LongestCount);
    }

    [Fact]
    public async Task GetAll_ReturnsEveryRegisteredDefinitionInOrder()
    {
        var weekly = new StreakDefinition { Id = "weekly", Period = StreakPeriod.Weekly };
        var ctx = Create(Daily, weekly);
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "weekly");

        IReadOnlyList<StreakProgress> all = await ctx.Service.GetAllAsync(ctx.PlayerId);

        Assert.Equal(new[] { "daily", "weekly" }, all.Select(p => p.DefinitionId));
        Assert.Equal(0, all[0].CurrentCount);
        Assert.Equal(1, all[1].CurrentCount);
    }

    [Fact]
    public async Task Reset_EndsRun_KeepsHistory()
    {
        var ctx = Create();
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily");
        await ctx.RecordAtAsync(Utc(2026, 11, 3), "daily");

        StreakResult reset = await ctx.Service.ResetAsync(ctx.PlayerId, "daily");
        StreakResult next = await ctx.RecordAtAsync(Utc(2026, 11, 4), "daily");

        Assert.Equal(StreakResultCode.Reset, reset.Code);
        Assert.Equal(StreakEventType.Reset, Assert.Single(reset.Events).Type);
        Assert.Equal(StreakStatus.NotStarted, reset.Progress.Status);
        Assert.Equal(2, reset.Progress.LongestCount);
        Assert.Equal(StreakResultCode.Started, next.Code);
        Assert.Equal(1, next.Progress.CurrentCount);
        Assert.Equal(2, next.Progress.LongestCount);
        Assert.Equal(3, next.Progress.TotalQualifyingActivities);
    }

    [Fact]
    public async Task Metadata_IsStoredPerStreak()
    {
        var ctx = Create();

        StreakResult set = await ctx.Service.SetMetadataAsync(ctx.PlayerId, "daily", "theme", "gold");
        StreakResult same = await ctx.Service.SetMetadataAsync(ctx.PlayerId, "daily", "theme", "gold");
        StreakProgress progress = await ctx.Service.GetProgressAsync(ctx.PlayerId, "daily");
        StreakResult removed = await ctx.Service.SetMetadataAsync(ctx.PlayerId, "daily", "theme", null);

        Assert.Equal(StreakResultCode.MetadataUpdated, set.Code);
        Assert.Equal(StreakResultCode.NoChange, same.Code);
        Assert.Equal("gold", progress.Metadata["theme"]);
        Assert.Empty(removed.Progress.Metadata);
    }

    [Fact]
    public async Task Delete_RemovesStoredStreak()
    {
        var ctx = Create();
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily");

        Assert.True(await ctx.Service.DeleteAsync(ctx.PlayerId, "daily"));
        Assert.False(await ctx.Service.DeleteAsync(ctx.PlayerId, "daily"));
        Assert.Null((await ctx.Service.GetProgressAsync(ctx.PlayerId, "daily")).StreakId);
    }

    [Fact]
    public async Task Players_AreIndependent()
    {
        var ctx = Create();
        var otherPlayer = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily");
        await ctx.RecordAtAsync(Utc(2026, 11, 3), "daily");
        await ctx.RecordAtAsync(Utc(2026, 11, 3), "daily", playerId: otherPlayer);

        Assert.Equal(2, (await ctx.Service.GetProgressAsync(ctx.PlayerId, "daily")).CurrentCount);
        Assert.Equal(1, (await ctx.Service.GetProgressAsync(otherPlayer, "daily")).CurrentCount);
    }
}
