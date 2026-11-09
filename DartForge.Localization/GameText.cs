using DartForge.Core.Models;
namespace DartForge.Localization;

/// <summary>
/// Maps culture-independent game identifiers (enums, codes, dart notation) to display text. Game logic never sees the
/// result: the engine works with <see cref="GameKind"/>, <see cref="GameMessageCode"/> and numbers only.
/// </summary>
public static class GameText
{
    public static string GameName(this ILocalizationService loc, GameKind kind) => loc.GetString($"GameMode_{kind}_Name");
    public static string GameRulesText(this ILocalizationService loc, GameKind kind) => loc.GetString($"GameMode_{kind}_Rules");

    /// <summary>Display name for a stored mode string (MatchRecord.Mode is the enum name); unknown values are shown as stored.</summary>
    public static string GameName(this ILocalizationService loc, string storedMode)
        => Enum.TryParse<GameKind>(storedMode, out var k) ? loc.GameName(k) : storedMode;

    /// <summary>Dart notation (T20, D16, 25, BULL) stays technical in every language; only a miss is translated.</summary>
    public static string DartLabel(this ILocalizationService loc, DartHit hit) => hit.Segment == 0 ? loc.GetString("Gameplay_MissButton") : hit.Label;

    /// <summary>Target label: numbers and dart notation stay as they are, "any double/triple" targets are translated.</summary>
    public static string TargetLabel(this ILocalizationService loc, TargetInfo target) => target switch
    {
        { Segment: null, Multiplier: 2 } => loc.GetString("Target_AnyDouble"),
        { Segment: null, Multiplier: 3 } => loc.GetString("Target_AnyTriple"),
        _ => target.Label
    };

    /// <summary>The localizable form of an engine event. Unknown codes fall back to the engine's English text.</summary>
    public static LocText Describe(GameEvent e) => e.Code switch
    {
        GameMessageCode.DartRecorded => new LocText("Event_DartRecorded", e.Subject ?? ""),
        GameMessageCode.Miss => new LocText("Event_Miss"),
        GameMessageCode.NotOpened => new LocText("Event_NotOpened"),
        GameMessageCode.Bust => new LocText("Event_Bust"),
        GameMessageCode.VisitSaved => new LocText("Event_VisitSaved"),
        GameMessageCode.Checkout => new LocText("Event_Checkout"),
        GameMessageCode.CricketAllClosed => new LocText("Event_CricketAllClosed"),
        GameMessageCode.AroundTheClockComplete => new LocText("Event_AroundTheClockComplete"),
        GameMessageCode.Shanghai => new LocText("Event_Shanghai"),
        GameMessageCode.TargetReached => new LocText("Event_TargetReached"),
        GameMessageCode.HalveItHalved => new LocText("Event_HalveItHalved"),
        GameMessageCode.Bob27NoDouble => new LocText("Event_Bob27NoDouble", e.Value),
        GameMessageCode.PlayerOut => new LocText("Event_PlayerOut", e.Subject ?? ""),
        _ => LocText.Literal(e.Message)
    };

    public static string Describe(this ILocalizationService loc, GameEvent e) => Describe(e).Resolve(loc);

    /// <summary>Resource key for an X01 in/out rule option.</summary>
    public static string InRuleKey(InRule rule) => $"Setup_In_{rule}";
    public static string OutRuleKey(OutRule rule) => $"Setup_Out_{rule}";
}
