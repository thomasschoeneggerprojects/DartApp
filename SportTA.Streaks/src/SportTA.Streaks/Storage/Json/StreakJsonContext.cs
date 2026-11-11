using System.Text.Json.Serialization;

namespace SportTA.Streaks.Storage.Json;

/// <summary>Source-generated serializer metadata (reflection-free, trimming- and AOT-safe, e.g. for MAUI Android).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StreakDocumentDto))]
internal sealed partial class StreakJsonContext : JsonSerializerContext
{
}
