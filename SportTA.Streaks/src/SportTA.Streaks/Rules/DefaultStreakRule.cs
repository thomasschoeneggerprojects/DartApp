namespace SportTA.Streaks.Rules;

/// <summary>
/// Built-in rule driven entirely by <see cref="StreakDefinition"/> settings:
/// <list type="bullet">
/// <item>activities outside <see cref="StreakDefinition.Categories"/> are ignored (case-insensitive; empty = all);</item>
/// <item>successful activities qualify if they reach <see cref="StreakDefinition.MinimumValue"/>;</item>
/// <item>failures break per-activity streaks and are ignored by calendar streaks;</item>
/// <item>neutral activities are ignored.</item>
/// </list>
/// </summary>
public sealed class DefaultStreakRule : IStreakRule
{
    private DefaultStreakRule()
    {
    }

    /// <summary>The shared, stateless instance.</summary>
    public static DefaultStreakRule Instance { get; } = new();

    /// <inheritdoc />
    public ActivityDecision Evaluate(StreakActivity activity, StreakDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Categories.Count > 0
            && (activity.Category is null || !definition.Categories.Contains(activity.Category, StringComparer.OrdinalIgnoreCase)))
        {
            return ActivityDecision.Ignore;
        }

        return activity.Outcome switch
        {
            ActivityOutcome.Success when definition.MinimumValue is { } minimum && (activity.Value is null || activity.Value < minimum)
                => ActivityDecision.Ignore,
            ActivityOutcome.Success => ActivityDecision.Qualify,
            ActivityOutcome.Failure when definition.Period == StreakPeriod.PerActivity => ActivityDecision.Fail,
            _ => ActivityDecision.Ignore,
        };
    }
}
