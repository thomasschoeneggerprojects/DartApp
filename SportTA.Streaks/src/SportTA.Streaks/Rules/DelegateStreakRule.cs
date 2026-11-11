namespace SportTA.Streaks.Rules;

/// <summary>Adapts a delegate to <see cref="IStreakRule"/> for small application-specific rules.</summary>
/// <example>
/// <code>
/// Rule = new DelegateStreakRule((activity, _) =&gt;
///     activity.Value &gt;= 60 ? ActivityDecision.Qualify : ActivityDecision.Ignore)
/// </code>
/// </example>
public sealed class DelegateStreakRule : IStreakRule
{
    private readonly Func<StreakActivity, StreakDefinition, ActivityDecision> _evaluate;

    /// <summary>Initializes a new instance. The delegate must be pure and thread-safe.</summary>
    public DelegateStreakRule(Func<StreakActivity, StreakDefinition, ActivityDecision> evaluate)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        _evaluate = evaluate;
    }

    /// <inheritdoc />
    public ActivityDecision Evaluate(StreakActivity activity, StreakDefinition definition) => _evaluate(activity, definition);
}
