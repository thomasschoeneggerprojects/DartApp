using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SportTA.Streaks.Storage.Json;

/// <summary>
/// Versioned JSON format. Older documents are migrated step by step in memory; documents from newer library
/// versions are rejected without modification.
/// </summary>
/// <remarks>
/// Version history:
/// <list type="bullet">
/// <item>0 – unversioned root array of streak records (pre-release format).</item>
/// <item>1 – <c>{ formatVersion, playerId, streaks: [...] }</c>.</item>
/// </list>
/// To add version N: bump <see cref="CurrentVersion"/> and add a <c>N-1 → N</c> step to <see cref="Migrate"/>.
/// </remarks>
internal static class StreakJsonFormat
{
    public const int CurrentVersion = 1;

    public static byte[] Serialize(Guid playerId, IEnumerable<Streak> streaks)
    {
        var document = new StreakDocumentDto
        {
            FormatVersion = CurrentVersion,
            PlayerId = playerId,
            Streaks = streaks.Select(ToDto).ToList(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(document, StreakJsonContext.Default.StreakDocumentDto);
    }

    /// <exception cref="JsonException">The data is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">The data is structurally invalid.</exception>
    /// <exception cref="StreakDataVersionException">The data was written by a newer version.</exception>
    public static List<Streak> Deserialize(byte[] data, Guid expectedPlayerId)
    {
        JsonNode root = JsonNode.Parse(data) ?? throw new InvalidDataException("The streak document is empty.");
        int version = GetFormatVersion(root);
        if (version > CurrentVersion)
        {
            throw new StreakDataVersionException(version, CurrentVersion);
        }

        if (version < 0)
        {
            throw new InvalidDataException($"Invalid format version {version}.");
        }

        while (version < CurrentVersion)
        {
            root = Migrate(version, root, expectedPlayerId);
            version++;
        }

        StreakDocumentDto document;
        try
        {
            document = JsonSerializer.Deserialize(root, StreakJsonContext.Default.StreakDocumentDto)
                ?? throw new InvalidDataException("The streak document is empty.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The streak document has an invalid structure.", ex);
        }

        if (document.PlayerId != expectedPlayerId)
        {
            throw new InvalidDataException("The streak document belongs to a different player.");
        }

        var streaks = new List<Streak>(document.Streaks?.Count ?? 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (StreakRecordDto? record in document.Streaks ?? [])
        {
            if (record is null)
            {
                throw new InvalidDataException("The streak document contains an empty record.");
            }

            Streak streak = FromDto(record, expectedPlayerId);
            if (!seen.Add(streak.DefinitionId))
            {
                throw new InvalidDataException($"Duplicate streak record '{streak.DefinitionId}'.");
            }

            streaks.Add(streak);
        }

        return streaks;
    }

    private static int GetFormatVersion(JsonNode root) => root switch
    {
        JsonArray => 0,
        JsonObject obj when obj["formatVersion"] is JsonValue value && value.TryGetValue(out int version) => version,
        _ => throw new InvalidDataException("The streak document has no format version."),
    };

    private static JsonNode Migrate(int fromVersion, JsonNode root, Guid playerId) => fromVersion switch
    {
        0 => new JsonObject
        {
            ["formatVersion"] = 1,
            ["playerId"] = playerId.ToString("D"),
            ["streaks"] = root,
        },
        _ => throw new InvalidDataException($"No migration from format version {fromVersion}."),
    };

    private static StreakRecordDto ToDto(Streak s) => new()
    {
        Id = s.Id,
        DefinitionId = s.DefinitionId,
        PlayerId = s.PlayerId,
        Status = s.Status,
        CurrentCount = s.CurrentCount,
        LongestCount = s.LongestCount,
        LongestCountAchievedAt = s.LongestCountAchievedAt,
        TotalQualifyingActivities = s.TotalQualifyingActivities,
        TotalQualifiedPeriods = s.TotalQualifiedPeriods,
        TimesBroken = s.TimesBroken,
        CurrentStreakStartedAt = s.CurrentStreakStartedAt,
        FirstActivityAt = s.FirstActivityAt,
        LastQualifyingActivityAt = s.LastQualifyingActivityAt,
        LastProcessedActivityAt = s.LastProcessedActivityAt,
        LastQualifiedPeriod = s.LastQualifiedPeriod,
        SettledThroughPeriod = s.SettledThroughPeriod,
        OpenPeriod = s.OpenPeriod,
        OpenPeriodActivityCount = s.OpenPeriodActivityCount,
        OpenPeriodActiveDays = NullIfEmpty(s.OpenPeriodActiveDays),
        ConsecutiveMissedPeriods = s.ConsecutiveMissedPeriods,
        FreezesAvailable = s.FreezesAvailable,
        FreezesUsed = s.FreezesUsed,
        RestorableCount = s.RestorableCount,
        RestorableStartedAt = s.RestorableStartedAt,
        RestorableMilestones = NullIfEmpty(s.RestorableMilestones),
        BrokenAt = s.BrokenAt,
        CurrentRunMilestones = NullIfEmpty(s.CurrentRunMilestones),
        AllTimeMilestones = NullIfEmpty(s.AllTimeMilestones),
        RecentActivityIds = NullIfEmpty(s.RecentActivityIds),
        Metadata = s.Metadata.Count == 0 ? null : new Dictionary<string, string>(s.Metadata, StringComparer.Ordinal),
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
        Version = s.Version,
    };

    private static Streak FromDto(StreakRecordDto d, Guid expectedPlayerId)
    {
        if (string.IsNullOrWhiteSpace(d.DefinitionId)
            || d.Id == Guid.Empty
            || d.PlayerId != expectedPlayerId
            || d.Version < 1
            || d.CurrentCount < 0
            || d.LongestCount < d.CurrentCount
            || d.OpenPeriodActivityCount < 0
            || d.FreezesAvailable < 0
            || d.FreezesUsed < 0
            || d.ConsecutiveMissedPeriods < 0
            || !Enum.IsDefined(d.Status))
        {
            throw new InvalidDataException($"Streak record '{d.DefinitionId}' is invalid.");
        }

        return new Streak
        {
            Id = d.Id,
            DefinitionId = d.DefinitionId,
            PlayerId = d.PlayerId,
            Status = d.Status,
            CurrentCount = d.CurrentCount,
            LongestCount = d.LongestCount,
            LongestCountAchievedAt = d.LongestCountAchievedAt?.ToUniversalTime(),
            TotalQualifyingActivities = d.TotalQualifyingActivities,
            TotalQualifiedPeriods = d.TotalQualifiedPeriods,
            TimesBroken = d.TimesBroken,
            CurrentStreakStartedAt = d.CurrentStreakStartedAt?.ToUniversalTime(),
            FirstActivityAt = d.FirstActivityAt?.ToUniversalTime(),
            LastQualifyingActivityAt = d.LastQualifyingActivityAt?.ToUniversalTime(),
            LastProcessedActivityAt = d.LastProcessedActivityAt?.ToUniversalTime(),
            LastQualifiedPeriod = d.LastQualifiedPeriod,
            SettledThroughPeriod = d.SettledThroughPeriod,
            OpenPeriod = d.OpenPeriod,
            OpenPeriodActivityCount = d.OpenPeriodActivityCount,
            OpenPeriodActiveDays = d.OpenPeriodActiveDays?.ToArray() ?? [],
            ConsecutiveMissedPeriods = d.ConsecutiveMissedPeriods,
            FreezesAvailable = d.FreezesAvailable,
            FreezesUsed = d.FreezesUsed,
            RestorableCount = d.RestorableCount,
            RestorableStartedAt = d.RestorableStartedAt?.ToUniversalTime(),
            RestorableMilestones = d.RestorableMilestones?.ToArray() ?? [],
            BrokenAt = d.BrokenAt?.ToUniversalTime(),
            CurrentRunMilestones = d.CurrentRunMilestones?.ToArray() ?? [],
            AllTimeMilestones = d.AllTimeMilestones?.ToArray() ?? [],
            RecentActivityIds = d.RecentActivityIds?.ToArray() ?? [],
            Metadata = d.Metadata is null
                ? ImmutableDictionary<string, string>.Empty
                : ImmutableDictionary.CreateRange(StringComparer.Ordinal, d.Metadata),
            CreatedAt = d.CreatedAt.ToUniversalTime(),
            UpdatedAt = d.UpdatedAt.ToUniversalTime(),
            Version = d.Version,
        };
    }

    private static List<T>? NullIfEmpty<T>(IReadOnlyList<T> values) => values.Count == 0 ? null : values.ToList();
}
