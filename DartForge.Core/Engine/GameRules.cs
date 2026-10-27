using DartForge.Core.Models;
namespace DartForge.Core.Engine;

/// <summary>Static rule data shared by the engine, the bot and the UI.</summary>
public static class GameRules
{
    /// <summary>Cricket numbers, in scoreboard order.</summary>
    public static readonly int[] CricketNumbers = [20, 19, 18, 17, 16, 15, 25];

    /// <summary>Halve It sequence. Missing the target with all three darts halves the score.</summary>
    public static readonly TargetInfo[] HalveItTargets =
    [
        new("15", 15, null), new("16", 16, null), new("Any Double", null, 2), new("17", 17, null),
        new("18", 18, null), new("Any Triple", null, 3), new("19", 19, null), new("20", 20, null), new("Bull", 25, null)
    ];

    /// <summary>Bob's 27: D1..D20, then the bullseye (21 rounds).</summary>
    public const int Bob27Rounds = 21;

    /// <summary>Clockwise board order starting at the top.</summary>
    public static readonly int[] BoardOrder = [20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5];

    public static TargetInfo ShanghaiTarget(int round) { var n = Math.Clamp(round, 1, 20); return new($"{n}", n, null); }
    public static TargetInfo HalveItTarget(int round) => HalveItTargets[Math.Clamp(round, 1, HalveItTargets.Length) - 1];
    public static TargetInfo Bob27Target(int round) => round >= Bob27Rounds ? new("Bull", 25, 2) : new($"D{Math.Max(1, round)}", Math.Max(1, round), 2);
    /// <summary>Points Bob's 27 adds per hit and subtracts when all three darts miss.</summary>
    public static int Bob27Value(int round) => round >= Bob27Rounds ? 50 : 2 * Math.Max(1, round);

    public static bool IsCricket(GameKind k) => k is GameKind.Cricket or GameKind.CutThroatCricket or GameKind.NoScoreCricket;
    public static bool IsRoundLimited(GameKind k) => k is not (GameKind.X01 or GameKind.Cricket or GameKind.CutThroatCricket or GameKind.NoScoreCricket);

    /// <summary>The number of rounds actually played for a game, taking fixed-length games into account.</summary>
    public static int TotalRounds(GameSettings s) => s.Kind switch
    {
        GameKind.HalveIt => HalveItTargets.Length,
        GameKind.Bob27 => Bob27Rounds,
        GameKind.Shanghai => Math.Clamp(s.MaxRounds, 1, 20),
        _ => s.MaxRounds
    };

    public static string Name(GameKind k) => k switch
    {
        GameKind.X01 => "X01", GameKind.Cricket => "Cricket", GameKind.CutThroatCricket => "Cut Throat Cricket",
        GameKind.NoScoreCricket => "No Score Cricket", GameKind.AroundTheClock => "Around the Clock", GameKind.Shanghai => "Shanghai",
        GameKind.CountUp => "Count Up", GameKind.HighScore => "High Score", GameKind.HalveIt => "Halve It", GameKind.Bob27 => "Bob's 27",
        _ => k.ToString()
    };

    public static string Rules(GameKind k) => k switch
    {
        GameKind.X01 => "Score down from the start score to exactly zero. Going below zero (or leaving 1 with a double/master out) is a bust and the visit does not count.",
        GameKind.Cricket => "Hit 15–20 and the bull three times each to open them. Extra hits on an open number score points while an opponent still has it open. Close everything with the most points to win.",
        GameKind.CutThroatCricket => "Like Cricket, but points on your open numbers are given to every opponent who hasn't closed them. Close everything with the fewest points to win.",
        GameKind.NoScoreCricket => "Pure race: the first player to close 15–20 and the bull wins. No points are scored.",
        GameKind.AroundTheClock => "Hit 1, 2, 3 … up to 20 in order. Any segment of the current number counts. First to hit 20 wins.",
        GameKind.Shanghai => "Round N targets number N. Singles, doubles and triples of the target score their value. Hitting single, double and triple of the target in one visit is a Shanghai and wins immediately.",
        GameKind.CountUp => "Every dart adds its value. First player to reach the target score wins.",
        GameKind.HighScore => "Every dart adds its value. Highest total after the last round wins.",
        GameKind.HalveIt => "Each round has a target (15, 16, any double, 17, 18, any triple, 19, 20, bull). Hits on the target score points; miss it with all three darts and your score is halved.",
        GameKind.Bob27 => "Start with 27. Round N targets double N (then the bull). Each hit adds the double's value; missing all three darts subtracts it. Drop to zero or below and you're out.",
        _ => ""
    };
}
