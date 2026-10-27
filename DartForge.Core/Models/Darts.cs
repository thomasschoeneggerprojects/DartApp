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
public sealed record GameSettings(GameKind Kind, int StartScore = 501, int MaxRounds = 20, OutRule Out = OutRule.Double, InRule In = InRule.Straight, int LegsToWin = 1, int SetsToWin = 1);
public sealed record PlayerInfo(Guid Id, string Name, bool IsBot = false)
{
    public static PlayerInfo Create(string name, bool isBot = false) => new(Guid.NewGuid(), name, isBot);
}
public sealed record Visit(Guid PlayerId, int Round, IReadOnlyList<DartHit> Darts, int Score, bool Bust, DateTimeOffset Timestamp);
public sealed record GameEvent(string Message, Guid? WinnerId = null);
public sealed record PlayerSnapshot(Guid Id, string Name, int Score, int Legs, int Sets, int DartsThrown, int Visits, int TotalScored, int CricketMarks, bool IsBot);
public sealed record GameSnapshot(GameSettings Settings, IReadOnlyList<PlayerSnapshot> Players, int ActivePlayer, int Round, int DartsInVisit, bool IsFinished, Guid? WinnerId, IReadOnlyList<Visit> Visits);
