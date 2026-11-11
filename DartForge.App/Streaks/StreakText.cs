using System.Globalization;
using DartForge.Localization;
using SportTA.Streaks;

namespace DartForge.Streaks;

/// <summary>
/// Turns DartTA streaks and streak events into <see cref="LocText"/> (resource key + arguments), so notices re-render
/// in a newly selected language like every other gameplay notice.
/// </summary>
public static class StreakText
{
    public const string MilestoneKey = "Event_StreakMilestone";
    public const string BrokenKey = "Event_StreakBroken";

    /// <summary>Localized display name of a streak definition.</summary>
    public static LocText Name(string definitionId) => definitionId switch
    {
        DartTaStreaks.DailyGoal => new LocText("Streak_DailyGoal_Name"),
        DartTaStreaks.WeeklyTraining => new LocText("Streak_WeeklyTraining_Name"),
        DartTaStreaks.MatchWinStreak => new LocText("Streak_MatchWins_Name"),
        _ => LocText.Literal(definitionId),
    };

    /// <summary>A streak length with its unit, e.g. "7 days", "3 weeks", "5 wins".</summary>
    public static LocText Count(string definitionId, int count)
    {
        string? unit = definitionId switch
        {
            DartTaStreaks.DailyGoal => "Days",
            DartTaStreaks.WeeklyTraining => "Weeks",
            DartTaStreaks.MatchWinStreak => "Wins",
            _ => null,
        };
        if (unit is null) return LocText.Literal(count.ToString(CultureInfo.InvariantCulture));
        // en/de/es share the integer plural rule "one" (exactly 1) / "other".
        return new LocText($"Streak_{unit}_{(count == 1 ? "One" : "Other")}", count);
    }

    /// <summary>The notice for a milestone or a broken streak; <see langword="null"/> for events DartTA does not announce.</summary>
    public static LocText? Describe(StreakEvent e) => e.Type switch
    {
        StreakEventType.MilestoneReached when e.Milestone is int milestone
            => new LocText(MilestoneKey, Name(e.DefinitionId), Count(e.DefinitionId, milestone)),
        StreakEventType.Broken when e.PreviousCount > 0
            => new LocText(BrokenKey, Name(e.DefinitionId), Count(e.DefinitionId, e.PreviousCount)),
        _ => null,
    };

    /// <summary>Notices for all announced events, in order.</summary>
    public static IEnumerable<LocText> Notices(IEnumerable<StreakEvent> events)
    {
        foreach (StreakEvent e in events)
        {
            if (Describe(e) is { } text) yield return text;
        }
    }
}
