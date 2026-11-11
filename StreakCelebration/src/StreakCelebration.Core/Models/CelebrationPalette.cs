using System.Globalization;

namespace StreakCelebration.Models;

/// <summary>
/// Colors of the overlay for one app theme, as hex strings (<c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c>).
/// Strings keep the core free of UI dependencies and make branding configurable from settings files.
/// </summary>
public sealed record CelebrationPalette
{
    /// <summary>Main accent: flame base, glow and particles.</summary>
    public string PrimaryColor { get; init; } = "#FF7A1A";

    /// <summary>Second accent: flame tip and particles.</summary>
    public string SecondaryColor { get; init; } = "#FFC93C";

    /// <summary>Background of the content card.</summary>
    public string CardColor { get; init; } = "#FFFFFF";

    /// <summary>Color of the large count.</summary>
    public string CountTextColor { get; init; } = "#E8650A";

    /// <summary>Headline color.</summary>
    public string TitleTextColor { get; init; } = "#1C1C1E";

    /// <summary>Message color.</summary>
    public string MessageTextColor { get; init; } = "#5A5A62";

    /// <summary>Dimming color; its strength is <see cref="StreakCelebrationOptions.BackgroundOpacity"/>.</summary>
    public string BackdropColor { get; init; } = "#0B0B10";

    /// <summary>Default colors for a light app theme.</summary>
    public static CelebrationPalette DefaultLight { get; } = new();

    /// <summary>Default colors for a dark app theme.</summary>
    public static CelebrationPalette DefaultDark { get; } = new()
    {
        PrimaryColor = "#FF8C1A",
        SecondaryColor = "#FFD54A",
        CardColor = "#1E242C",
        CountTextColor = "#FFC93C",
        TitleTextColor = "#FFFFFF",
        MessageTextColor = "#C3CCD6",
        BackdropColor = "#000000",
    };

    /// <summary>All colors with their property names, for validation and resource lookup.</summary>
    public IEnumerable<KeyValuePair<string, string>> Entries()
    {
        yield return new(nameof(PrimaryColor), PrimaryColor);
        yield return new(nameof(SecondaryColor), SecondaryColor);
        yield return new(nameof(CardColor), CardColor);
        yield return new(nameof(CountTextColor), CountTextColor);
        yield return new(nameof(TitleTextColor), TitleTextColor);
        yield return new(nameof(MessageTextColor), MessageTextColor);
        yield return new(nameof(BackdropColor), BackdropColor);
    }

    /// <summary>Returns validation errors, prefixed with <paramref name="paletteName"/>.</summary>
    public IEnumerable<string> Validate(string paletteName)
    {
        foreach ((string name, string value) in Entries())
        {
            if (!TryParseColor(value, out _))
            {
                yield return $"{paletteName}.{name} '{value}' is not a hex color (#RGB, #RRGGBB or #AARRGGBB).";
            }
        }
    }

    /// <summary>Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c> into ARGB components.</summary>
    public static bool TryParseColor(string? value, out (byte A, byte R, byte G, byte B) argb)
    {
        argb = default;
        if (string.IsNullOrWhiteSpace(value) || value[0] != '#') return false;
        ReadOnlySpan<char> hex = value.AsSpan(1);
        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint raw)) return false;

        switch (hex.Length)
        {
            case 3:
                argb = (255, Expand((raw >> 8) & 0xF), Expand((raw >> 4) & 0xF), Expand(raw & 0xF));
                return true;
            case 6:
                argb = (255, (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
                return true;
            case 8:
                argb = ((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
                return true;
            default:
                return false;
        }

        static byte Expand(uint nibble) => (byte)(nibble * 17);
    }
}
