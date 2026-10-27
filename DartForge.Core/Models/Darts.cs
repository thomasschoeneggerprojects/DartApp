namespace DartForge.Core.Models;
public enum GameKind { X01, Cricket, CutThroatCricket, NoScoreCricket, AroundTheClock, Shanghai, CountUp, HighScore, HalveIt, Bob27 }
public enum EntryMode { DartByDart, VisitTotal }
public enum OutRule { Straight, Double, Master }
public enum InRule { Straight, Double, Master }
public readonly record struct DartHit(int Segment, int Multiplier)
{
    public int Score => Segment == 25 ? 25 * Multiplier : Segment * Multiplier;
    public string Label => Segment == 0 ? "MISS" : Segment == 25 ? (Multiplier == 2 ? "BULL" : "25") : $"{(Multiplier == 3 ? "T" : Multiplier == 2 ? "D" : "S")}{Segment}";
    public static DartHit Miss => new(0, 0);
    public bool IsValid => Segment == 0 ? Multiplier == 0 : Segment == 25 ? Multiplier is 1 or 2 : Segment is >= 1 and <= 20 && Multiplier is >= 1 and <= 3;
}
/// <summary>
/// Settings for a game. <see cref="StartScore"/> is the X01 start score and the Count Up target score.
/// <see cref="MaxRounds"/> limits round-based games (Shanghai, High Score, Around the Clock, ...).
/// </summary>
public sealed record GameSettings(GameKind Kind, int StartScore = 501, int MaxRounds = 20, OutRule Out = OutRule.Double, InRule In = InRule.Straight, int LegsToWin = 1, int SetsToWin = 1);
public sealed record PlayerInfo(Guid Id, string Name, bool IsBot = false)
{
    public static PlayerInfo Create(string name, bool isBot = false) => new(Guid.NewGuid(), name, isBot);
}
/// <summary>A completed visit. For X01 <see cref="Score"/> is the raw visit total; for all other games it is the change of the player's game score.</summary>
public sealed record Visit(Guid PlayerId, int Round, IReadOnlyList<DartHit> Darts, int Score, bool Bust, DateTimeOffset Timestamp);
public sealed record GameEvent(string Message, Guid? WinnerId = null);
public sealed record PlayerSnapshot(Guid Id, string Name, int Score, int Legs, int Sets, int DartsThrown, int Visits, int TotalScored, int CricketMarks, bool IsBot,
    IReadOnlyDictionary<int, int>? Marks = null, bool Eliminated = false, bool Opened = true)
{
    /// <summary>Cricket marks (0-3) on a number (15-20, 25).</summary>
    public int MarksOn(int segment) => Marks is not null && Marks.TryGetValue(segment, out var m) ? m : 0;
}
public sealed record GameSnapshot(GameSettings Settings, IReadOnlyList<PlayerSnapshot> Players, int ActivePlayer, int Round, int DartsInVisit, bool IsFinished, Guid? WinnerId, IReadOnlyList<Visit> Visits);

/// <summary>What the active player has to hit. Null segment/multiplier means "any".</summary>
public sealed record TargetInfo(string Label, int? Segment, int? Multiplier)
{
    public bool Matches(DartHit hit) => hit.Segment != 0 && (Segment is null || hit.Segment == Segment) && (Multiplier is null || hit.Multiplier == Multiplier);
}
