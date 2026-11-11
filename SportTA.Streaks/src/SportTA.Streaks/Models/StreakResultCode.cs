namespace SportTA.Streaks;

/// <summary>Primary outcome of a streak operation. Secondary effects are reported as <see cref="StreakEvent"/>s.</summary>
public enum StreakResultCode
{
    /// <summary>Nothing changed.</summary>
    NoChange = 0,

    /// <summary>The activity started a new streak run.</summary>
    Started,

    /// <summary>The activity increased the streak.</summary>
    Increased,

    /// <summary>The activity counted towards the current period, but the period requirement is not met yet.</summary>
    PeriodProgress,

    /// <summary>The current period (or, for distinct-day weekly streaks, the current day) has already been counted.</summary>
    AlreadyCountedForPeriod,

    /// <summary>The activity identifier was already processed. Nothing changed.</summary>
    DuplicateActivity,

    /// <summary>The streak rule ignored the activity.</summary>
    NotQualifying,

    /// <summary>The activity belongs to a period (or time) that has already been settled and can no longer change the streak.</summary>
    LateActivity,

    /// <summary>The activity timestamp lies too far in the future. Nothing changed.</summary>
    RejectedFutureTimestamp,

    /// <summary>A failure was recorded while no streak was running.</summary>
    FailureRecorded,

    /// <summary>A failure was absorbed by a grace period or a freeze.</summary>
    FailureAbsorbed,

    /// <summary>The streak was broken.</summary>
    Broken,

    /// <summary>The streak was evaluated and its state changed (e.g. grace or freezes were consumed).</summary>
    Evaluated,

    /// <summary>Freezes were added.</summary>
    FreezesAdded,

    /// <summary>A broken streak was restored.</summary>
    Restored,

    /// <summary>There was no broken streak to restore.</summary>
    NothingToRestore,

    /// <summary>The streak was reset.</summary>
    Reset,

    /// <summary>Application metadata was updated.</summary>
    MetadataUpdated,
}
