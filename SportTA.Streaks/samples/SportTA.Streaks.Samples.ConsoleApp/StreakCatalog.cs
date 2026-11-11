using SportTA.Streaks;
using SportTA.Streaks.Rules;

namespace SportTA.Streaks.Samples.ConsoleApp;

/// <summary>Application-owned streak definitions. The library contains no game-specific rules.</summary>
internal static class StreakCatalog
{
    public static StreakDefinition DailyTraining { get; } = new()
    {
        Id = "daily-training",
        Name = "Daily Training",
        Period = StreakPeriod.Daily,
        RequiredActivities = 1,
        Categories = ["training"],
        MinimumValue = 30,
        GracePeriods = 0,
        MaxFreezes = 2,
        Milestones = [3, 7, 14, 30, 100],
    };

    public static StreakDefinition WeeklyWorkouts { get; } = new()
    {
        Id = "weekly-workouts",
        Name = "3 sessions per week",
        Period = StreakPeriod.Weekly,
        RequiredActivities = 3,
        WeekStartDay = DayOfWeek.Monday,
        Milestones = [4, 12, 52],
    };

    public static StreakDefinition X01WinStreak { get; } = new()
    {
        Id = "x01-win-streak",
        Name = "X01 win streak",
        Period = StreakPeriod.PerActivity,
        Milestones = [3, 5, 10],
        Rule = new DelegateStreakRule((activity, _) => activity switch
        {
            { Category: not "x01" } => ActivityDecision.Ignore,
            { Outcome: ActivityOutcome.Success } => ActivityDecision.Qualify,
            { Outcome: ActivityOutcome.Failure } => ActivityDecision.Fail,
            _ => ActivityDecision.Ignore,
        }),
    };
}
