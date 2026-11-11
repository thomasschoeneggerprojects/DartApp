using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class GraceFreezeRestoreTests
{
    private static StreakTestContext Create(StreakDefinition definition) => new(Utc(2026, 11, 2), [definition]);

    [Fact]
    public async Task GracePeriod_ToleratesOneMissedDay()
    {
        var ctx = Create(new StreakDefinition { Id = "d", GracePeriods = 1 });
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "d");

        StreakResult evaluated = await ctx.EvaluateAtAsync(Utc(2026, 11, 4, 8), "d");
        StreakResult recorded = await ctx.RecordAtAsync(Utc(2026, 11, 4, 9), "d");

        Assert.Equal(StreakResultCode.Evaluated, evaluated.Code);
        Assert.Equal(StreakStatus.InGrace, evaluated.Progress.Status);
        Assert.Equal(0, evaluated.Progress.GracePeriodsRemaining);
        Assert.Empty(evaluated.Events);
        Assert.Equal(StreakResultCode.Increased, recorded.Code);
        Assert.Equal(2, recorded.Progress.CurrentCount);
        Assert.Equal(StreakStatus.Active, recorded.Progress.Status);
        Assert.Equal(1, recorded.Progress.GracePeriodsRemaining);
    }

    [Fact]
    public async Task GracePeriod_Exhausted_BreaksStreak()
    {
        var ctx = Create(new StreakDefinition { Id = "d", GracePeriods = 1 });
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "d");

        StreakResult result = await ctx.RecordAtAsync(Utc(2026, 11, 5), "d");

        Assert.Equal(new[] { StreakEventType.Broken, StreakEventType.Started }, result.Events.Select(e => e.Type));
    }

    [Fact]
    public async Task GracePeriod_IsRenewedByQualifyingDay()
    {
        var ctx = Create(new StreakDefinition { Id = "d", GracePeriods = 1 });
        StreakResult? last = null;
        foreach (int day in new[] { 2, 4, 6, 8 })
        {
            last = await ctx.RecordAtAsync(Utc(2026, 11, day), "d");
        }

        Assert.Equal(4, last!.Progress.CurrentCount);
    }

    [Fact]
    public async Task Freeze_CoversMissedDay()
    {
        var ctx = Create(new StreakDefinition { Id = "d" });
        StreakResult added = await ctx.Service.AddFreezesAsync(ctx.PlayerId, "d", 1);
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "d");

        StreakResult result = await ctx.RecordAtAsync(Utc(2026, 11, 4), "d");

        Assert.Equal(StreakResultCode.FreezesAdded, added.Code);
        Assert.Equal(new[] { StreakEventType.Frozen, StreakEventType.Increased }, result.Events.Select(e => e.Type));
        Assert.Equal(1, result.Events[0].FreezesConsumed);
        Assert.Equal(2, result.Progress.CurrentCount);
        Assert.Equal(0, result.Progress.FreezesAvailable);
        Assert.Equal(1, (await ctx.Service.GetStatisticsAsync(ctx.PlayerId, "d")).FreezesUsed);
    }

    [Fact]
    public async Task InsufficientFreezes_AreNotWasted()
    {
        var ctx = Create(new StreakDefinition { Id = "d" });
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "d");
        await ctx.Service.AddFreezesAsync(ctx.PlayerId, "d", 1);

        StreakResult result = await ctx.RecordAtAsync(Utc(2026, 11, 5), "d");

        Assert.Equal(new[] { StreakEventType.Broken, StreakEventType.Started }, result.Events.Select(e => e.Type));
        Assert.Equal(1, result.Progress.FreezesAvailable);
    }

    [Fact]
    public async Task GraceIsUsedBeforeFreezes()
    {
        var ctx = Create(new StreakDefinition { Id = "d", GracePeriods = 1 });
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "d");
        await ctx.Service.AddFreezesAsync(ctx.PlayerId, "d", 1);

        StreakResult result = await ctx.RecordAtAsync(Utc(2026, 11, 5), "d");

        Assert.Equal(new[] { StreakEventType.Frozen, StreakEventType.Increased }, result.Events.Select(e => e.Type));
        Assert.Equal(2, result.Progress.CurrentCount);
        Assert.Equal(0, result.Progress.FreezesAvailable);
    }

    [Fact]
    public async Task MaxFreezes_CapsBalance()
    {
        var ctx = Create(new StreakDefinition { Id = "d", MaxFreezes = 2 });

        StreakResult added = await ctx.Service.AddFreezesAsync(ctx.PlayerId, "d", 5);
        StreakResult more = await ctx.Service.AddFreezesAsync(ctx.PlayerId, "d", 1);

        Assert.Equal(2, added.Progress.FreezesAvailable);
        Assert.Equal(StreakResultCode.NoChange, more.Code);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.Service.AddFreezesAsync(ctx.PlayerId, "d", 0));
    }

    [Fact]
    public async Task Restore_BringsBackBrokenStreak_AndAllowsContinuingToday()
    {
        var ctx = Create(new StreakDefinition { Id = "d", Milestones = [3] });
        foreach (int day in new[] { 2, 3, 4 })
        {
            await ctx.RecordAtAsync(Utc(2026, 11, day), "d");
        }

        await ctx.EvaluateAtAsync(Utc(2026, 11, 6, 9), "d");
        StreakResult restored = await ctx.Service.RestoreAsync(ctx.PlayerId, "d");
        StreakResult today = await ctx.RecordAtAsync(Utc(2026, 11, 6, 10), "d");

        Assert.Equal(StreakResultCode.Restored, restored.Code);
        Assert.Equal(StreakEventType.Restored, Assert.Single(restored.Events).Type);
        Assert.Equal(3, restored.Progress.CurrentCount);
        Assert.Equal(StreakStatus.Active, restored.Progress.Status);
        Assert.Null(restored.Progress.RestorableCount);
        Assert.Equal(StreakResultCode.Increased, today.Code);
        Assert.Equal(4, today.Progress.CurrentCount);
    }

    [Fact]
    public async Task Restore_MergesWithNewRun_AndReportsNewMilestones()
    {
        var ctx = Create(new StreakDefinition { Id = "d", Milestones = [3] });
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "d");
        await ctx.RecordAtAsync(Utc(2026, 11, 3), "d");
        await ctx.RecordAtAsync(Utc(2026, 11, 5), "d");

        StreakResult restored = await ctx.Service.RestoreAsync(ctx.PlayerId, "d");

        Assert.Equal(3, restored.Progress.CurrentCount);
        Assert.Equal(new[] { StreakEventType.Restored, StreakEventType.MilestoneReached }, restored.Events.Select(e => e.Type));
        Assert.True(restored.Events[1].IsFirstTimeMilestone);
    }

    [Fact]
    public async Task Restore_WithoutBrokenStreak_DoesNothing()
    {
        var ctx = Create(new StreakDefinition { Id = "d" });
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "d");

        StreakResult result = await ctx.Service.RestoreAsync(ctx.PlayerId, "d");

        Assert.Equal(StreakResultCode.NothingToRestore, result.Code);
        Assert.False(result.StateChanged);
        Assert.Empty(result.Events);
    }
}
