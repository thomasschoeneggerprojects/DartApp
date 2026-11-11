using SportTA.Streaks.Rules;

namespace SportTA.Streaks;

/// <summary>
/// Immutable description of a streak type, registered once by the hosting application
/// (for example "daily-training" or "x01-win-streak").
/// </summary>
public sealed record StreakDefinition
{
    /// <summary>Maximum length of <see cref="Id"/>.</summary>
    public const int MaxIdLength = 100;

    /// <summary>Unique, stable identifier. Used as the storage key, so it should never change.</summary>
    public required string Id { get; init; }

    /// <summary>Optional human-readable name. Localization is the application's job.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How activities are grouped. Defaults to <see cref="StreakPeriod.Daily"/>.</summary>
    public StreakPeriod Period { get; init; } = StreakPeriod.Daily;

    /// <summary>Qualifying activities (or distinct active days, see <see cref="CountDistinctDays"/>) required per period. Must be 1 for <see cref="StreakPeriod.PerActivity"/>.</summary>
    public int RequiredActivities { get; init; } = 1;

    /// <summary>Weekly streaks only: count distinct active days instead of activities (e.g. "exercise on 5 days per week").</summary>
    public bool CountDistinctDays { get; init; }

    /// <summary>Weekly streaks only: first day of the week.</summary>
    public DayOfWeek WeekStartDay { get; init; } = DayOfWeek.Monday;

    /// <summary>
    /// Number of consecutive missed periods (or failures for <see cref="StreakPeriod.PerActivity"/>) that are tolerated without
    /// breaking the streak. Missed periods never increase the count. The allowance is renewed by every qualifying period.
    /// </summary>
    public int GracePeriods { get; init; }

    /// <summary>Maximum number of freezes a player can hold for this streak. <see langword="null"/> means unlimited.</summary>
    public int? MaxFreezes { get; init; }

    /// <summary>Streak counts that raise <see cref="StreakEventType.MilestoneReached"/>, e.g. <c>[3, 7, 14, 30, 100]</c>.</summary>
    public IReadOnlyList<int> Milestones { get; init; } = [];

    /// <summary>Activity categories accepted by the default rule (case-insensitive). Empty means every category.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Default rule only: minimum <see cref="StreakActivity.Value"/> for a successful activity to qualify.</summary>
    public double? MinimumValue { get; init; }

    /// <summary>Custom qualification rule. <see langword="null"/> uses <see cref="DefaultStreakRule"/>.</summary>
    public IStreakRule? Rule { get; init; }

    /// <summary>Gets <see cref="Name"/>, or <see cref="Id"/> when no name is set.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    internal IStreakRule EffectiveRule => Rule ?? DefaultStreakRule.Instance;

    /// <summary>Validates the definition.</summary>
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("A streak definition requires a non-empty Id.", nameof(Id));
        }

        if (Id.Length > MaxIdLength)
        {
            throw new ArgumentException($"The streak definition Id must not exceed {MaxIdLength} characters.", nameof(Id));
        }

        if (!Enum.IsDefined(Period))
        {
            throw new ArgumentException($"Unknown streak period '{Period}'.", nameof(Period));
        }

        if (!Enum.IsDefined(WeekStartDay))
        {
            throw new ArgumentException($"Unknown week start day '{WeekStartDay}'.", nameof(WeekStartDay));
        }

        if (RequiredActivities < 1)
        {
            throw new ArgumentException("RequiredActivities must be at least 1.", nameof(RequiredActivities));
        }

        if (Period == StreakPeriod.PerActivity && RequiredActivities != 1)
        {
            throw new ArgumentException("Per-activity streaks require RequiredActivities = 1.", nameof(RequiredActivities));
        }

        if (CountDistinctDays && Period != StreakPeriod.Weekly)
        {
            throw new ArgumentException("CountDistinctDays is only supported for weekly streaks.", nameof(CountDistinctDays));
        }

        if (CountDistinctDays && RequiredActivities > 7)
        {
            throw new ArgumentException("A week has at most 7 distinct days.", nameof(RequiredActivities));
        }

        if (GracePeriods < 0)
        {
            throw new ArgumentException("GracePeriods must not be negative.", nameof(GracePeriods));
        }

        if (MaxFreezes is < 0)
        {
            throw new ArgumentException("MaxFreezes must not be negative.", nameof(MaxFreezes));
        }

        if (Milestones is null || Milestones.Any(m => m < 1))
        {
            throw new ArgumentException("Milestones must be positive numbers.", nameof(Milestones));
        }

        if (Categories is null || Categories.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Categories must not contain empty values.", nameof(Categories));
        }

        if (MinimumValue is { } minimum && !double.IsFinite(minimum))
        {
            throw new ArgumentException("MinimumValue must be a finite number.", nameof(MinimumValue));
        }
    }

    /// <summary>Creates a defensive copy with sorted, distinct milestones.</summary>
    internal StreakDefinition Normalize() => this with
    {
        Milestones = Milestones.Distinct().Order().ToArray(),
        Categories = Categories.ToArray(),
    };
}
