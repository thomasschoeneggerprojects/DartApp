namespace SportTA.Streaks;

/// <summary>An activity reported by the application (a workout, a training session, a finished match, ...).</summary>
public sealed record StreakActivity
{
    /// <summary>Maximum length of <see cref="Id"/>.</summary>
    public const int MaxIdLength = 200;

    /// <summary>
    /// Unique identifier of the activity. Reusing the same identifier makes processing idempotent:
    /// retries and repeated synchronization never count an activity twice.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>When the activity happened. Normalized to UTC; calendar periods use the player's time zone.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Outcome of the activity. Defaults to <see cref="ActivityOutcome.Success"/>.</summary>
    public ActivityOutcome Outcome { get; init; } = ActivityOutcome.Success;

    /// <summary>Optional application-defined category (e.g. "cycling", "x01").</summary>
    public string? Category { get; init; }

    /// <summary>Optional numeric value (e.g. minutes, distance, score).</summary>
    public double? Value { get; init; }

    /// <summary>Optional application-defined data for custom rules. Not persisted.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Creates an activity with a random identifier. Use this only when the activity is recorded exactly once;
    /// for retries or synchronization, supply a stable <see cref="Id"/> instead.
    /// </summary>
    public static StreakActivity Create(
        DateTimeOffset timestamp,
        ActivityOutcome outcome = ActivityOutcome.Success,
        string? category = null,
        double? value = null) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = timestamp,
            Outcome = outcome,
            Category = category,
            Value = value,
        };

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("The activity requires a non-empty Id.", nameof(Id));
        }

        if (Id.Length > MaxIdLength)
        {
            throw new ArgumentException($"The activity Id must not exceed {MaxIdLength} characters.", nameof(Id));
        }

        if (Timestamp == default)
        {
            throw new ArgumentException("The activity requires a timestamp.", nameof(Timestamp));
        }

        if (!Enum.IsDefined(Outcome))
        {
            throw new ArgumentException($"Unknown activity outcome '{Outcome}'.", nameof(Outcome));
        }

        if (Value is { } value && !double.IsFinite(value))
        {
            throw new ArgumentException("The activity value must be a finite number.", nameof(Value));
        }
    }
}
