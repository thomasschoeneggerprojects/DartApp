using DartForge.Localization;
using Microsoft.Extensions.Logging;
using SportTA.Streaks;
using StreakCelebration.Models;
using StreakCelebration.Services;

namespace DartForge.Streaks;

/// <summary>
/// DartTA's bridge from SportTA.Streaks events to the reusable celebration overlay. The streak library stays the source
/// of truth: this only reads the events it publishes (after they were persisted) and picks a celebration level.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="StreakEventType.MilestoneReached"/> → <see cref="CelebrationLevel.Record"/> the first time that milestone was ever
/// reached (a new personal best), otherwise <see cref="CelebrationLevel.Milestone"/>.</item>
/// <item><see cref="StreakEventType.Increased"/> to 2 or more → <see cref="CelebrationLevel.Normal"/>, except at a configured milestone,
/// whose <see cref="StreakEventType.MilestoneReached"/> event follows and gets the bigger celebration instead.</item>
/// <item>Broken streaks stay quiet banner notices (<see cref="StreakText"/>), never a celebration.</item>
/// </list>
/// </remarks>
public sealed class DartTaCelebrationHandler(
    IStreakCelebrationService celebrations,
    ILocalizationService localization,
    ILogger<DartTaCelebrationHandler> logger) : IStreakEventHandler
{
    public Task HandleAsync(StreakEvent streakEvent, CancellationToken cancellationToken)
    {
        if (StreakCelebrationMapping.LevelFor(streakEvent) is not { } level) return Task.CompletedTask;

        StreakCelebrationData data = StreakCelebrationMapping.ToCelebration(streakEvent, level, localization);
        // Not awaited: a celebration lasts seconds and recording a training or match must not wait for it.
        // The caller's token belongs to the recording call, so it is not passed on.
        _ = ShowAsync(data);
        return Task.CompletedTask;
    }

    private async Task ShowAsync(StreakCelebrationData data)
    {
        try
        {
            await celebrations.ShowAsync(data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Streak celebration '{Title}' could not be shown.", data.Title);
        }
    }
}

/// <summary>Maps a streak event to a celebration; pure, so the rules above are easy to follow.</summary>
public static class StreakCelebrationMapping
{
    /// <summary>The celebration level, or <see langword="null"/> for events DartTA does not celebrate.</summary>
    public static CelebrationLevel? LevelFor(StreakEvent e) => e.Type switch
    {
        StreakEventType.MilestoneReached => e.IsFirstTimeMilestone ? CelebrationLevel.Record : CelebrationLevel.Milestone,
        StreakEventType.Increased when e.CurrentCount >= 2 && !IsConfiguredMilestone(e) => CelebrationLevel.Normal,
        _ => null,
    };

    /// <summary>Localized celebration text, e.g. "NEW RECORD!" / "Daily goal streak: 7 days! Keep it going."</summary>
    public static StreakCelebrationData ToCelebration(StreakEvent e, CelebrationLevel level, ILocalizationService localization)
    {
        int count = e.Milestone ?? e.CurrentCount;
        string titleKey = level switch
        {
            CelebrationLevel.Record => "Celebration_Title_Record",
            CelebrationLevel.Milestone => "Celebration_Title_Milestone",
            _ => "Celebration_Title_Normal",
        };

        return new StreakCelebrationData
        {
            Count = count,
            Level = level,
            Title = new LocText(titleKey).Resolve(localization),
            Message = new LocText(StreakText.MilestoneKey, StreakText.Name(e.DefinitionId), StreakText.Count(e.DefinitionId, count))
                .Resolve(localization),
        };
    }

    // A milestone at this count fires its own MilestoneReached event right after this Increased event.
    private static bool IsConfiguredMilestone(StreakEvent e)
        => DartTaStreaks.Definitions.FirstOrDefault(d => d.Id == e.DefinitionId)?.Milestones.Contains(e.CurrentCount) == true;
}
