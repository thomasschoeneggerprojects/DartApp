namespace SportTA.Streaks;

/// <summary>Thrown when an operation refers to a streak definition that is not registered.</summary>
public sealed class StreakDefinitionNotFoundException : StreakException
{
    /// <summary>Initializes a new instance.</summary>
    public StreakDefinitionNotFoundException(string definitionId)
        : base($"No streak definition with id '{definitionId}' is registered.")
    {
        DefinitionId = definitionId;
    }

    /// <summary>The unknown definition id.</summary>
    public string DefinitionId { get; }
}
