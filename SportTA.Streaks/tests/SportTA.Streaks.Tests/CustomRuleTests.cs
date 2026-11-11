using SportTA.Streaks.Rules;
using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class CustomRuleTests
{
    private static readonly DateTimeOffset Start = Utc(2026, 11, 2, 10);

    /// <summary>An application-defined rule implemented outside the library.</summary>
    private sealed class CyclingSessionRule : IStreakRule
    {
        public ActivityDecision Evaluate(StreakActivity activity, StreakDefinition definition)
            => activity.Category == "cycling" && activity.Value >= 20 ? ActivityDecision.Qualify : ActivityDecision.Ignore;
    }

    [Fact]
    public async Task ClassRule_TenCyclingSessions_ReachesMilestone()
    {
        var definition = new StreakDefinition
        {
            Id = "ten-rides",
            Period = StreakPeriod.PerActivity,
            Milestones = [10],
            Rule = new CyclingSessionRule(),
        };
        var ctx = new StreakTestContext(Start, [definition]);

        StreakResult shortRide = await ctx.RecordAtAsync(Start, "ten-rides", category: "cycling", value: 5);
        StreakResult? last = null;
        for (int i = 1; i <= 10; i++)
        {
            last = await ctx.RecordAtAsync(Start.AddHours(i), "ten-rides", category: "cycling", value: 30);
        }

        Assert.Equal(StreakResultCode.NotQualifying, shortRide.Code);
        Assert.Equal(10, last!.Progress.CurrentCount);
        Assert.Equal(10, Assert.Single(last.Events, e => e.Type == StreakEventType.MilestoneReached).Milestone);
    }

    [Fact]
    public async Task DelegateRule_ThreeConsecutiveDartWins_FromMetadata()
    {
        var definition = new StreakDefinition
        {
            Id = "dart-wins",
            Period = StreakPeriod.PerActivity,
            Milestones = [3],
            Rule = new DelegateStreakRule((activity, _) => activity.Metadata?.GetValueOrDefault("result") switch
            {
                "win" => ActivityDecision.Qualify,
                "loss" => ActivityDecision.Fail,
                _ => ActivityDecision.Ignore,
            }),
        };
        var ctx = new StreakTestContext(Start, [definition]);
        var win = new Dictionary<string, string> { ["result"] = "win" };
        var loss = new Dictionary<string, string> { ["result"] = "loss" };

        await ctx.RecordAtAsync(Start, "dart-wins", metadata: win);
        await ctx.RecordAtAsync(Start.AddMinutes(1), "dart-wins", metadata: win);
        StreakResult third = await ctx.RecordAtAsync(Start.AddMinutes(2), "dart-wins", metadata: win);
        StreakResult lost = await ctx.RecordAtAsync(Start.AddMinutes(3), "dart-wins", metadata: loss);

        Assert.Contains(third.Events, e => e.Type == StreakEventType.MilestoneReached && e.Milestone == 3);
        Assert.Equal(StreakResultCode.Broken, lost.Code);
    }

    [Fact]
    public async Task MinimumValue_OnDefaultRule()
    {
        var definition = new StreakDefinition { Id = "30-min", MinimumValue = 30 };
        var ctx = new StreakTestContext(Start, [definition]);

        StreakResult tooShort = await ctx.RecordAtAsync(Start, "30-min", value: 10);
        StreakResult noValue = await ctx.RecordAtAsync(Start.AddMinutes(1), "30-min");
        StreakResult enough = await ctx.RecordAtAsync(Start.AddMinutes(2), "30-min", value: 45);

        Assert.Equal(StreakResultCode.NotQualifying, tooShort.Code);
        Assert.Equal(StreakResultCode.NotQualifying, noValue.Code);
        Assert.Equal(StreakResultCode.Started, enough.Code);
    }

    [Fact]
    public async Task CustomRule_CanFailCalendarStreak_EvenInAlreadyCountedDay()
    {
        var definition = new StreakDefinition
        {
            Id = "clean-days",
            Rule = new DelegateStreakRule((activity, _) =>
                activity.Category == "violation" ? ActivityDecision.Fail : ActivityDecision.Qualify),
        };
        var ctx = new StreakTestContext(Start, [definition]);
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "clean-days");
        await ctx.RecordAtAsync(Utc(2026, 11, 3), "clean-days");

        StreakResult violation = await ctx.RecordAtAsync(Utc(2026, 11, 3, 20), "clean-days", category: "violation");
        StreakResult restored = await ctx.Service.RestoreAsync(ctx.PlayerId, "clean-days");
        StreakResult sameDay = await ctx.RecordAtAsync(Utc(2026, 11, 3, 21), "clean-days");

        Assert.Equal(StreakResultCode.Broken, violation.Code);
        Assert.Equal(2, restored.Progress.CurrentCount);
        Assert.Equal(StreakResultCode.AlreadyCountedForPeriod, sameDay.Code);
        Assert.Equal(2, sameDay.Progress.CurrentCount);
    }

    [Fact]
    public async Task RuleReturningUndefinedDecision_Throws_AndPersistsNothing()
    {
        var definition = new StreakDefinition { Id = "broken-rule", Rule = new DelegateStreakRule((_, _) => (ActivityDecision)99) };
        var ctx = new StreakTestContext(Start, [definition]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.RecordAtAsync(Start, "broken-rule"));
        Assert.Equal(0, ((InMemoryStreakRepository)ctx.Repository).Count);
    }
}
