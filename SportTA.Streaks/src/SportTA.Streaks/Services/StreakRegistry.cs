using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace SportTA.Streaks;

/// <summary>
/// Thread-safe catalog of streak definitions. Registration order is preserved and used wherever the library
/// processes several streaks, which keeps results deterministic.
/// </summary>
public sealed class StreakRegistry
{
    private readonly object _gate = new();
    private ImmutableDictionary<string, StreakDefinition> _byId = ImmutableDictionary.Create<string, StreakDefinition>(StringComparer.Ordinal);
    private ImmutableList<StreakDefinition> _definitions = ImmutableList<StreakDefinition>.Empty;

    /// <summary>Initializes an empty registry.</summary>
    public StreakRegistry()
    {
    }

    /// <summary>Initializes a registry with definitions.</summary>
    public StreakRegistry(IEnumerable<StreakDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (StreakDefinition definition in definitions)
        {
            Register(definition);
        }
    }

    /// <summary>All registered definitions in registration order.</summary>
    public IReadOnlyList<StreakDefinition> Definitions => Volatile.Read(ref _definitions);

    /// <summary>Validates and registers a definition. A defensive copy is stored.</summary>
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    /// <exception cref="InvalidOperationException">A definition with the same id is already registered.</exception>
    public StreakRegistry Register(StreakDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        StreakDefinition normalized = definition.Normalize();

        lock (_gate)
        {
            if (_byId.ContainsKey(normalized.Id))
            {
                throw new InvalidOperationException($"A streak definition with id '{normalized.Id}' is already registered.");
            }

            Volatile.Write(ref _byId, _byId.Add(normalized.Id, normalized));
            Volatile.Write(ref _definitions, _definitions.Add(normalized));
        }

        return this;
    }

    /// <summary>Looks up a definition.</summary>
    public bool TryGet(string id, [NotNullWhen(true)] out StreakDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Volatile.Read(ref _byId).TryGetValue(id, out definition);
    }

    /// <summary>Gets a definition.</summary>
    /// <exception cref="StreakDefinitionNotFoundException">The id is not registered.</exception>
    public StreakDefinition Get(string id)
        => TryGet(id, out StreakDefinition? definition) ? definition : throw new StreakDefinitionNotFoundException(id);
}
