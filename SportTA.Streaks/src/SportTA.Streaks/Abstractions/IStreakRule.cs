namespace SportTA.Streaks;

/// <summary>
/// Decides how an activity affects a streak. Implement this to add application-specific qualification
/// (e.g. "only won x01 legs with an average above 60") without modifying the library.
/// Rules must be pure and thread-safe: same input, same decision, no side effects.
/// </summary>
public interface IStreakRule
{
    /// <summary>Classifies an activity for the given definition.</summary>
    ActivityDecision Evaluate(StreakActivity activity, StreakDefinition definition);
}
