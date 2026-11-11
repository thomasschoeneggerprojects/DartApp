using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class ConsecutiveActivityStreakTests
{
    private static readonly StreakDefinition WinStreak = new()
    {
        Id = "wins",
        Period = StreakPeriod.PerActivity,
        Milestones = [3, 5],
    };

    private static readonly DateTimeOffset Start = Utc(2026, 11, 2, 10);

    [Fact]
    public async Task Wins_IncreaseStreak_LossBreaksIt()
    {
        var ctx = new StreakTestContext(Start, [WinStreak]);
        for (int i = 0; i < 3; i++)
        {
            await ctx.RecordAtAsync(Start.AddMinutes(i), "wins");
        }

        StreakResult loss = await ctx.RecordAtAsync(Start.AddMinutes(10), "wins", outcome: ActivityOutcome.Failure);

        Assert.Equal(StreakResultCode.Broken, loss.Code);
        Assert.Equal(StreakEventType.Broken, Assert.Single(loss.Events).Type);
        Assert.Equal(0, loss.Progress.CurrentCount);
        Assert.Equal(3, loss.Progress.LongestCount);
    }

    [Fact]
    public async Task FiveConsecutiveWins_ReachMilestone()
    {
        var ctx = new StreakTestContext(Start, [WinStreak]);
        StreakResult? last = null;
        for (int i = 0; i < 5; i++)
        {
            last = await ctx.RecordAtAsync(Start.AddMinutes(i), "wins");
        }

        Assert.Equal(new[] { StreakEventType.Increased, StreakEventType.MilestoneReached }, last!.Events.Select(e => e.Type));
        Assert.Equal(5, last.Events[1].Milestone);
    }

    [Fact]
    public async Task NeutralOutcome_IsIgnored()
    {
        var ctx = new StreakTestContext(Start, [WinStreak]);
        await ctx.RecordAtAsync(Start, "wins");

        StreakResult draw = await ctx.RecordAtAsync(Start.AddMinutes(1), "wins", outcome: ActivityOutcome.Neutral);

        Assert.Equal(StreakResultCode.NotQualifying, draw.Code);
        Assert.Equal(1, draw.Progress.CurrentCount);
    }

    [Fact]
    public async Task Categories_FilterActivities_CaseInsensitive()
    {
        var definition = WinStreak with { Id = "x01-wins", Categories = ["x01"] };
        var ctx = new StreakTestContext(Start, [definition]);

        StreakResult cricket = await ctx.RecordAtAsync(Start, "x01-wins", category: "cricket");
        StreakResult noCategory = await ctx.RecordAtAsync(Start.AddMinutes(1), "x01-wins");
        StreakResult x01 = await ctx.RecordAtAsync(Start.AddMinutes(2), "x01-wins", category: "X01");

        Assert.Equal(StreakResultCode.NotQualifying, cricket.Code);
        Assert.Equal(StreakResultCode.NotQualifying, noCategory.Code);
        Assert.Equal(StreakResultCode.Started, x01.Code);
    }

    [Fact]
    public async Task Failure_WithoutRunningStreak_IsRecordedWithoutEvents()
    {
        var ctx = new StreakTestContext(Start, [WinStreak]);

        StreakResult loss = await ctx.RecordAtAsync(Start, "wins", outcome: ActivityOutcome.Failure);

        Assert.Equal(StreakResultCode.FailureRecorded, loss.Code);
        Assert.Empty(loss.Events);
    }

    [Fact]
    public async Task GracePeriod_AbsorbsOneFailure()
    {
        var definition = WinStreak with { Id = "forgiving", GracePeriods = 1 };
        var ctx = new StreakTestContext(Start, [definition]);
        await ctx.RecordAtAsync(Start, "forgiving");
        await ctx.RecordAtAsync(Start.AddMinutes(1), "forgiving");

        StreakResult loss = await ctx.RecordAtAsync(Start.AddMinutes(2), "forgiving", outcome: ActivityOutcome.Failure);
        StreakResult win = await ctx.RecordAtAsync(Start.AddMinutes(3), "forgiving");
        StreakResult secondLoss = await ctx.RecordAtAsync(Start.AddMinutes(4), "forgiving", outcome: ActivityOutcome.Failure);
        StreakResult thirdLoss = await ctx.RecordAtAsync(Start.AddMinutes(5), "forgiving", outcome: ActivityOutcome.Failure);

        Assert.Equal(StreakResultCode.FailureAbsorbed, loss.Code);
        Assert.Equal(StreakStatus.InGrace, loss.Progress.Status);
        Assert.Equal(2, loss.Progress.CurrentCount);
        Assert.Equal(StreakResultCode.Increased, win.Code);
        Assert.Equal(StreakStatus.Active, win.Progress.Status);
        Assert.Equal(StreakResultCode.FailureAbsorbed, secondLoss.Code);
        Assert.Equal(StreakResultCode.Broken, thirdLoss.Code);
        Assert.Equal(3, thirdLoss.Progress.LongestCount);
    }

    [Fact]
    public async Task OutOfOrderActivity_IsLate_AndDoesNotChangeStreak()
    {
        var ctx = new StreakTestContext(Start, [WinStreak]);
        await ctx.RecordAtAsync(Start.AddMinutes(10), "wins");

        StreakResult late = await ctx.Service.RecordActivityAsync(ctx.PlayerId, "wins", new StreakActivity
        {
            Id = "older",
            Timestamp = Start,
            Outcome = ActivityOutcome.Failure,
        });

        Assert.Equal(StreakResultCode.LateActivity, late.Code);
        Assert.Equal(1, late.Progress.CurrentCount);
    }

    [Fact]
    public async Task TimePassing_DoesNotBreakPerActivityStreak()
    {
        var ctx = new StreakTestContext(Start, [WinStreak]);
        await ctx.RecordAtAsync(Start, "wins");

        StreakResult result = await ctx.EvaluateAtAsync(Start.AddYears(1), "wins");

        Assert.Equal(StreakResultCode.NoChange, result.Code);
        Assert.Equal(1, result.Progress.CurrentCount);
        Assert.Null(result.Progress.CurrentPeriodStart);
    }
}
