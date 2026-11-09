namespace DartForge.Localization;

/// <summary>
/// A message stored as resource key + arguments instead of a finished string, so it can be re-rendered in a new
/// language at any time (e.g. the last gameplay notice when the user switches language mid-match).
/// Arguments may themselves be <see cref="LocText"/> values; they are resolved recursively.
/// </summary>
public sealed record LocText(string Key, params object[] Args)
{
    public string Resolve(ILocalizationService loc)
    {
        if (Args.Length == 0) return loc.GetString(Key);
        var resolved = new object[Args.Length];
        for (var i = 0; i < Args.Length; i++) resolved[i] = Args[i] is LocText inner ? inner.Resolve(loc) : Args[i];
        return loc.GetString(Key, resolved);
    }

    /// <summary>A literal, untranslated value (e.g. a player name) wrapped as text.</summary>
    public static LocText Literal(string value) => new("Common_Literal", value);
}
