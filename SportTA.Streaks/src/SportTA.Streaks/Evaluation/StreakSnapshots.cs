namespace SportTA.Streaks.Evaluation;

/// <summary>Builds read-only views from an (already evaluated) streak state.</summary>
internal static class StreakSnapshots
{
    public static StreakProgress CreateProgress(Streak state, StreakDefinition definition, TimeZoneInfo timeZone, DateTimeOffset now, bool persisted)
    {
        int inPeriod = 0;
        bool complete = false;
        DateTimeOffset? periodStart = null;
        DateTimeOffset? periodEnd = null;

        if (definition.Period != StreakPeriod.PerActivity)
        {
            long current = StreakPeriods.GetPeriod(definition, now, timeZone);
            var bounds = StreakPeriods.GetBounds(definition, current, timeZone);
            periodStart = bounds.Start;
            periodEnd = bounds.End;
            complete = state.LastQualifiedPeriod == current;
            inPeriod = state.OpenPeriod == current ? state.OpenPeriodActivityCount : 0;
        }

        int? nextMilestone = null;
        foreach (int milestone in definition.Milestones)
        {
            if (milestone > state.CurrentCount)
            {
                nextMilestone = milestone;
                break;
            }
        }

        return new StreakProgress
        {
            PlayerId = state.PlayerId,
            DefinitionId = definition.Id,
            Name = definition.DisplayName,
            StreakId = persisted ? state.Id : null,
            Period = definition.Period,
            Status = state.Status,
            CurrentCount = state.CurrentCount,
            LongestCount = state.LongestCount,
            TotalQualifyingActivities = state.TotalQualifyingActivities,
            RequiredActivities = definition.RequiredActivities,
            ActivitiesInCurrentPeriod = inPeriod,
            IsCurrentPeriodComplete = complete,
            IsAtRisk = definition.Period != StreakPeriod.PerActivity && state.CurrentCount > 0 && !complete,
            CurrentPeriodStart = periodStart,
            CurrentPeriodEnd = periodEnd,
            CurrentStreakStartedAt = state.CurrentStreakStartedAt,
            LastQualifyingActivityAt = state.LastQualifyingActivityAt,
            FreezesAvailable = state.FreezesAvailable,
            GracePeriodsRemaining = Math.Max(0, definition.GracePeriods - state.ConsecutiveMissedPeriods),
            NextMilestone = nextMilestone,
            ReachedMilestones = state.CurrentRunMilestones,
            RestorableCount = state.RestorableCount,
            BrokenAt = state.BrokenAt,
            Metadata = state.Metadata,
        };
    }

    public static StreakStatistics CreateStatistics(Streak state) => new()
    {
        PlayerId = state.PlayerId,
        DefinitionId = state.DefinitionId,
        CurrentCount = state.CurrentCount,
        LongestCount = state.LongestCount,
        LongestCountAchievedAt = state.LongestCountAchievedAt,
        TotalQualifyingActivities = state.TotalQualifyingActivities,
        TotalQualifiedPeriods = state.TotalQualifiedPeriods,
        TimesBroken = state.TimesBroken,
        FreezesUsed = state.FreezesUsed,
        FreezesAvailable = state.FreezesAvailable,
        AllTimeMilestones = state.AllTimeMilestones,
        FirstActivityAt = state.FirstActivityAt,
        LastQualifyingActivityAt = state.LastQualifyingActivityAt,
    };
}
