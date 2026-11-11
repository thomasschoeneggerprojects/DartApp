using System.Collections.Immutable;

namespace SportTA.Streaks.Evaluation;

/// <summary>
/// Pure state machine for one streak. Takes an immutable <see cref="Streak"/>, applies one operation and exposes the new
/// state plus the events it produced. It performs no I/O, reads no clock and has no shared state; the service persists
/// the result and only then publishes the events.
/// </summary>
internal sealed class StreakMutation
{
    private readonly StreakDefinition _definition;
    private readonly TimeZoneInfo _timeZone;
    private readonly DateTimeOffset _now;
    private readonly int _activityHistorySize;
    private readonly List<StreakEvent> _events = [];
    private string? _activityId;
    private bool _broken;

    public StreakMutation(Streak state, StreakDefinition definition, TimeZoneInfo timeZone, DateTimeOffset now, int activityHistorySize)
    {
        State = state;
        _definition = definition;
        _timeZone = timeZone;
        _now = now;
        _activityHistorySize = activityHistorySize;
    }

    public Streak State { get; private set; }

    public bool Changed { get; private set; }

    public StreakResultCode Code { get; private set; } = StreakResultCode.NoChange;

    public IReadOnlyList<StreakEvent> Events => _events;

    private bool IsPeriodic => _definition.Period != StreakPeriod.PerActivity;

    /// <summary>Settles every period that ended before now.</summary>
    public void Evaluate()
    {
        SettleUntilNow();
        Code = _broken ? StreakResultCode.Broken : Changed ? StreakResultCode.Evaluated : StreakResultCode.NoChange;
    }

    public void SettleUntilNow()
    {
        if (IsPeriodic)
        {
            SettleBefore(StreakPeriods.GetPeriod(_definition, _now, _timeZone));
        }
    }

    /// <summary>Applies an activity. The activity timestamp must already be UTC.</summary>
    public void RecordActivity(StreakActivity activity, TimeSpan maxFutureSkew)
    {
        _activityId = activity.Id;
        DateTimeOffset timestamp = activity.Timestamp;

        if (timestamp > _now + maxFutureSkew)
        {
            Code = StreakResultCode.RejectedFutureTimestamp;
            return;
        }

        if (State.RecentActivityIds.Contains(activity.Id, StringComparer.Ordinal))
        {
            Code = StreakResultCode.DuplicateActivity;
            return;
        }

        ActivityDecision decision = _definition.EffectiveRule.Evaluate(activity, _definition);
        if (!Enum.IsDefined(decision))
        {
            throw new InvalidOperationException($"Streak rule for '{_definition.Id}' returned undefined decision '{decision}'.");
        }

        RememberActivity(activity.Id);

        if (decision == ActivityDecision.Ignore)
        {
            Code = StreakResultCode.NotQualifying;
            return;
        }

        if (IsPeriodic)
        {
            RecordPeriodic(decision, timestamp);
        }
        else
        {
            RecordPerActivity(decision, timestamp);
        }
    }

    public void AddFreezes(int count)
    {
        SettleUntilNow();
        int cap = _definition.MaxFreezes ?? int.MaxValue;
        int available = (int)Math.Min((long)State.FreezesAvailable + count, cap);
        if (available == State.FreezesAvailable)
        {
            Code = StreakResultCode.NoChange;
            return;
        }

        Apply(State with { FreezesAvailable = available });
        Code = StreakResultCode.FreezesAdded;
    }

    public void Restore()
    {
        SettleUntilNow();
        Streak s = State;
        if (s.RestorableCount is not int restorable || restorable <= 0)
        {
            Code = StreakResultCode.NothingToRestore;
            return;
        }

        int merged = restorable + s.CurrentCount;
        bool newLongest = merged > s.LongestCount;
        Streak next = s with
        {
            CurrentCount = merged,
            Status = StreakStatus.Active,
            ConsecutiveMissedPeriods = 0,
            CurrentStreakStartedAt = s.RestorableStartedAt ?? s.CurrentStreakStartedAt,
            CurrentRunMilestones = s.RestorableMilestones.Union(s.CurrentRunMilestones).Order().ToArray(),
            RestorableCount = null,
            RestorableStartedAt = null,
            RestorableMilestones = [],
            BrokenAt = null,
            LongestCount = newLongest ? merged : s.LongestCount,
            LongestCountAchievedAt = newLongest ? _now : s.LongestCountAchievedAt,
        };

        if (IsPeriodic && s.CurrentCount == 0)
        {
            // Treat everything before the current period as covered, so the player can continue today.
            long current = StreakPeriods.GetPeriod(_definition, _now, _timeZone);
            long anchor = Math.Max(current - 1, s.SettledThroughPeriod ?? long.MinValue);
            next = next with { LastQualifiedPeriod = anchor, SettledThroughPeriod = anchor };
        }

        Apply(next);
        AddEvent(StreakEventType.Restored, s.CurrentCount);
        Code = StreakResultCode.Restored;
        ReachMilestones();
    }

    public void Reset()
    {
        Streak s = State;
        if (s.CurrentCount == 0 && s.RestorableCount is null && s.OpenPeriodActivityCount == 0 && s.Status == StreakStatus.NotStarted)
        {
            Code = StreakResultCode.NoChange;
            return;
        }

        Apply(s with
        {
            Status = StreakStatus.NotStarted,
            CurrentCount = 0,
            CurrentStreakStartedAt = null,
            CurrentRunMilestones = [],
            ConsecutiveMissedPeriods = 0,
            RestorableCount = null,
            RestorableStartedAt = null,
            RestorableMilestones = [],
            BrokenAt = null,
            LastQualifiedPeriod = null,
            SettledThroughPeriod = null,
            OpenPeriod = null,
            OpenPeriodActivityCount = 0,
            OpenPeriodActiveDays = [],
        });
        AddEvent(StreakEventType.Reset, s.CurrentCount);
        Code = StreakResultCode.Reset;
    }

    public void SetMetadata(string key, string? value)
    {
        var metadata = ImmutableDictionary.CreateRange(StringComparer.Ordinal, State.Metadata);
        ImmutableDictionary<string, string> updated = value is null ? metadata.Remove(key) : metadata.SetItem(key, value);
        if (updated.Count == metadata.Count && (value is null || (metadata.TryGetValue(key, out string? old) && old == value)))
        {
            Code = StreakResultCode.NoChange;
            return;
        }

        Apply(State with { Metadata = updated });
        Code = StreakResultCode.MetadataUpdated;
    }

    private void RecordPerActivity(ActivityDecision decision, DateTimeOffset timestamp)
    {
        if (State.LastProcessedActivityAt is { } last && timestamp < last)
        {
            Code = StreakResultCode.LateActivity;
            return;
        }

        Apply(State with { LastProcessedActivityAt = timestamp });

        if (decision == ActivityDecision.Fail)
        {
            ApplyFailure();
            return;
        }

        CountQualifyingActivity(timestamp);
        Apply(State with { TotalQualifiedPeriods = State.TotalQualifiedPeriods + 1 });
        Increase(timestamp);
    }

    private void RecordPeriodic(ActivityDecision decision, DateTimeOffset timestamp)
    {
        long period = StreakPeriods.GetPeriod(_definition, timestamp, _timeZone);
        long? settled = State.SettledThroughPeriod;

        if (decision == ActivityDecision.Fail)
        {
            if (settled is { } settledForFailure && period < settledForFailure)
            {
                Code = StreakResultCode.LateActivity;
                return;
            }

            SettleBefore(period);
            TrackProcessed(timestamp);
            ApplyFailure();
            return;
        }

        if (settled is { } settledPeriod && period <= settledPeriod)
        {
            if (period == State.LastQualifiedPeriod)
            {
                CountQualifyingActivity(timestamp);
                Code = StreakResultCode.AlreadyCountedForPeriod;
            }
            else
            {
                Code = StreakResultCode.LateActivity;
            }

            return;
        }

        SettleBefore(period);
        TrackProcessed(timestamp);
        CountQualifyingActivity(timestamp);

        if (State.OpenPeriod != period)
        {
            Apply(State with { OpenPeriod = period, OpenPeriodActivityCount = 0, OpenPeriodActiveDays = [] });
        }

        if (_definition.CountDistinctDays)
        {
            int day = StreakPeriods.GetDayNumber(timestamp, _timeZone);
            if (State.OpenPeriodActiveDays.Contains(day))
            {
                Code = StreakResultCode.AlreadyCountedForPeriod;
                return;
            }

            int[] days = [.. State.OpenPeriodActiveDays, day];
            Apply(State with { OpenPeriodActiveDays = days, OpenPeriodActivityCount = days.Length });
        }
        else
        {
            Apply(State with { OpenPeriodActivityCount = State.OpenPeriodActivityCount + 1 });
        }

        if (State.OpenPeriodActivityCount < _definition.RequiredActivities)
        {
            Code = StreakResultCode.PeriodProgress;
            return;
        }

        Apply(State with
        {
            LastQualifiedPeriod = period,
            SettledThroughPeriod = period,
            TotalQualifiedPeriods = State.TotalQualifiedPeriods + 1,
        });
        Increase(timestamp);
    }

    /// <summary>Settles all periods before <paramref name="period"/>; unqualified ones count as missed.</summary>
    private void SettleBefore(long period)
    {
        if (State.CurrentCount == 0 || State.SettledThroughPeriod is not { } settled)
        {
            return;
        }

        long lastToSettle = period - 1;
        if (lastToSettle <= settled)
        {
            return;
        }

        Streak next = State with { SettledThroughPeriod = lastToSettle };
        if (next.OpenPeriod is { } open && open <= lastToSettle)
        {
            next = next with { OpenPeriod = null, OpenPeriodActivityCount = 0, OpenPeriodActiveDays = [] };
        }

        Apply(next);
        ApplyMisses(lastToSettle - settled);
    }

    /// <summary>Covers misses with grace periods first, then freezes; otherwise breaks the streak.</summary>
    /// <returns><see langword="true"/> when all misses were absorbed.</returns>
    private bool ApplyMisses(long missed)
    {
        Streak s = State;
        long graceLeft = Math.Max(0, _definition.GracePeriods - s.ConsecutiveMissedPeriods);
        long graceUsed = Math.Min(missed, graceLeft);
        long remaining = missed - graceUsed;

        // Freezes are only spent if they actually save the streak.
        if (remaining > s.FreezesAvailable)
        {
            Break();
            return false;
        }

        int freezesUsed = (int)remaining;
        Apply(s with
        {
            ConsecutiveMissedPeriods = s.ConsecutiveMissedPeriods + (int)graceUsed,
            FreezesAvailable = s.FreezesAvailable - freezesUsed,
            FreezesUsed = s.FreezesUsed + freezesUsed,
            Status = graceUsed > 0 ? StreakStatus.InGrace : s.Status,
        });

        if (freezesUsed > 0)
        {
            AddEvent(StreakEventType.Frozen, s.CurrentCount, freezesConsumed: freezesUsed);
        }

        return true;
    }

    private void ApplyFailure()
    {
        if (State.CurrentCount == 0)
        {
            Code = StreakResultCode.FailureRecorded;
            return;
        }

        Code = ApplyMisses(1) ? StreakResultCode.FailureAbsorbed : StreakResultCode.Broken;
    }

    private void Break()
    {
        Streak s = State;
        Apply(s with
        {
            Status = StreakStatus.Broken,
            CurrentCount = 0,
            CurrentStreakStartedAt = null,
            CurrentRunMilestones = [],
            ConsecutiveMissedPeriods = 0,
            TimesBroken = s.TimesBroken + 1,
            RestorableCount = s.CurrentCount,
            RestorableStartedAt = s.CurrentStreakStartedAt,
            RestorableMilestones = s.CurrentRunMilestones,
            BrokenAt = _now,
        });
        _broken = true;
        AddEvent(StreakEventType.Broken, s.CurrentCount);
    }

    private void Increase(DateTimeOffset timestamp)
    {
        Streak s = State;
        bool starting = s.CurrentCount == 0;
        int count = s.CurrentCount + 1;
        bool newLongest = count > s.LongestCount;
        Apply(s with
        {
            CurrentCount = count,
            Status = StreakStatus.Active,
            ConsecutiveMissedPeriods = 0,
            CurrentStreakStartedAt = starting ? timestamp : s.CurrentStreakStartedAt,
            LongestCount = newLongest ? count : s.LongestCount,
            LongestCountAchievedAt = newLongest ? timestamp : s.LongestCountAchievedAt,
        });

        AddEvent(starting ? StreakEventType.Started : StreakEventType.Increased, s.CurrentCount);
        Code = starting ? StreakResultCode.Started : StreakResultCode.Increased;
        ReachMilestones();
    }

    private void ReachMilestones()
    {
        foreach (int milestone in _definition.Milestones)
        {
            if (milestone > State.CurrentCount)
            {
                break;
            }

            if (State.CurrentRunMilestones.Contains(milestone))
            {
                continue;
            }

            bool firstTime = !State.AllTimeMilestones.Contains(milestone);
            Apply(State with
            {
                CurrentRunMilestones = [.. State.CurrentRunMilestones, milestone],
                AllTimeMilestones = firstTime ? [.. State.AllTimeMilestones, milestone] : State.AllTimeMilestones,
            });
            AddEvent(StreakEventType.MilestoneReached, State.CurrentCount, milestone, firstTime);
        }
    }

    private void CountQualifyingActivity(DateTimeOffset timestamp)
    {
        Streak s = State;
        Apply(s with
        {
            TotalQualifyingActivities = s.TotalQualifyingActivities + 1,
            FirstActivityAt = s.FirstActivityAt is { } first && first <= timestamp ? first : timestamp,
            LastQualifyingActivityAt = s.LastQualifyingActivityAt is { } last && last >= timestamp ? last : timestamp,
        });
    }

    private void TrackProcessed(DateTimeOffset timestamp)
    {
        if (State.LastProcessedActivityAt is not { } last || timestamp > last)
        {
            Apply(State with { LastProcessedActivityAt = timestamp });
        }
    }

    private void RememberActivity(string activityId)
    {
        IReadOnlyList<string> ids = State.RecentActivityIds;
        int keep = Math.Min(ids.Count, _activityHistorySize - 1);
        var next = new string[keep + 1];
        for (int i = 0; i < keep; i++)
        {
            next[i] = ids[ids.Count - keep + i];
        }

        next[keep] = activityId;
        Apply(State with { RecentActivityIds = next });
    }

    private void AddEvent(StreakEventType type, int previousCount, int? milestone = null, bool firstTime = false, int freezesConsumed = 0)
    {
        _events.Add(new StreakEvent
        {
            Type = type,
            PlayerId = State.PlayerId,
            DefinitionId = State.DefinitionId,
            StreakId = State.Id,
            CurrentCount = State.CurrentCount,
            PreviousCount = previousCount,
            Milestone = milestone,
            IsFirstTimeMilestone = firstTime,
            FreezesConsumed = freezesConsumed,
            ActivityId = _activityId,
            OccurredAt = _now,
        });
    }

    private void Apply(Streak next)
    {
        State = next;
        Changed = true;
    }
}
