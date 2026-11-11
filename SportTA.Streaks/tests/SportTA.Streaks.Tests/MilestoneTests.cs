using SportTA.Streaks.Storage;
using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class MilestoneTests
{
    private static readonly StreakDefinition Daily = new() { Id = "d", Milestones = [7, 3, 3] };

    [Fact]
    public async Task Milestone_IsReportedOncePerRun()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [Daily]);
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "d");
        await ctx.RecordAtAsync(Utc(2026, 11, 3), "d");

        StreakResult third = await ctx.RecordAtAsync(Utc(2026, 11, 4), "d");
        StreakResult sameDay = await ctx.RecordAtAsync(Utc(2026, 11, 4, 20), "d");
        StreakResult fourth = await ctx.RecordAtAsync(Utc(2026, 11, 5), "d");

        StreakEvent milestone = Assert.Single(third.Events, e => e.Type == StreakEventType.MilestoneReached);
        Assert.Equal(3, milestone.Milestone);
        Assert.True(milestone.IsFirstTimeMilestone);
        Assert.Empty(sameDay.Events);
        Assert.DoesNotContain(fourth.Events, e => e.Type == StreakEventType.MilestoneReached);
        Assert.Equal(7, fourth.Progress.NextMilestone);
        Assert.Equal(new[] { 3 }, fourth.Progress.ReachedMilestones);
        Assert.Single(ctx.Handler.Events, e => e.Type == StreakEventType.MilestoneReached);
    }

    [Fact]
    public async Task Milestone_InNewRun_IsReportedAsRepeat()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [Daily]);
        foreach (int day in new[] { 2, 3, 4, 10, 11 })
        {
            await ctx.RecordAtAsync(Utc(2026, 11, day), "d");
        }

        StreakResult result = await ctx.RecordAtAsync(Utc(2026, 11, 12), "d");

        StreakEvent milestone = Assert.Single(result.Events, e => e.Type == StreakEventType.MilestoneReached);
        Assert.False(milestone.IsFirstTimeMilestone);
        Assert.Equal(new[] { 3 }, (await ctx.Service.GetStatisticsAsync(ctx.PlayerId, "d")).AllTimeMilestones);
    }

    [Fact]
    public async Task Restore_DoesNotRepeatMilestonesOfRestoredRun()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [Daily]);
        foreach (int day in new[] { 2, 3, 4 })
        {
            await ctx.RecordAtAsync(Utc(2026, 11, day), "d");
        }

        await ctx.EvaluateAtAsync(Utc(2026, 11, 7), "d");
        StreakResult restored = await ctx.Service.RestoreAsync(ctx.PlayerId, "d");

        Assert.Equal(new[] { StreakEventType.Restored }, restored.Events.Select(e => e.Type));
    }

    [Fact]
    public async Task ReachedMilestones_SurviveRestart_SoRetriesDoNotReward()
    {
        using var dir = new TempDirectory();
        var first = new StreakTestContext(Utc(2026, 11, 2), [Daily], repository: new JsonStreakRepository(new FileStreakStorageProvider(dir.Path)));
        await first.RecordAtAsync(Utc(2026, 11, 2), "d", "a1");
        await first.RecordAtAsync(Utc(2026, 11, 3), "d", "a2");
        StreakResult reached = await first.RecordAtAsync(Utc(2026, 11, 4), "d", "a3");

        var second = new StreakTestContext(Utc(2026, 11, 4), [Daily], repository: new JsonStreakRepository(new FileStreakStorageProvider(dir.Path)));
        StreakResult retry = await second.RecordAtAsync(Utc(2026, 11, 4), "d", "a3");
        StreakResult otherSameDay = await second.RecordAtAsync(Utc(2026, 11, 4, 18), "d", "a4");

        Assert.Contains(reached.Events, e => e.Type == StreakEventType.MilestoneReached);
        Assert.Equal(StreakResultCode.DuplicateActivity, retry.Code);
        Assert.Empty(retry.Events);
        Assert.Empty(otherSameDay.Events);
        Assert.Empty(second.Handler.Events);
    }
}
