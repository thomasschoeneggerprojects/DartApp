using DartForge.Core.Engine;
using DartForge.Core.Models;
using Xunit;
namespace DartForge.Tests;

public sealed class GameModeTests
{
    private static DartHit S(int n) => new(n, 1);
    private static DartHit D(int n) => new(n, 2);
    private static DartHit T(int n) => new(n, 3);
    private static readonly DartHit M = DartHit.Miss;

    private static DartGame Solo(GameSettings s) => new(s, [PlayerInfo.Create("Alice")]);

    [Fact] public void CricketMarksAndScores()
    {
        var a = PlayerInfo.Create("A"); var b = PlayerInfo.Create("B");
        var g = new DartGame(new GameSettings(GameKind.Cricket), [a, b]);
        g.Throw(T(20)); g.Throw(T(20)); g.Throw(S(19));
        var p = g.Snapshot.Players[0];
        Assert.Equal(3, p.MarksOn(20)); Assert.Equal(1, p.MarksOn(19)); Assert.Equal(60, p.Score);
    }

    [Fact] public void CutThroatGivesPointsToOpponents()
    {
        var g = new DartGame(new GameSettings(GameKind.CutThroatCricket), [PlayerInfo.Create("A"), PlayerInfo.Create("B")]);
        g.Throw(T(20)); g.Throw(S(20));
        Assert.Equal(0, g.Snapshot.Players[0].Score); Assert.Equal(20, g.Snapshot.Players[1].Score);
    }

    [Fact] public void NoScoreCricketWinsWhenAllClosed()
    {
        var g = Solo(new GameSettings(GameKind.NoScoreCricket));
        foreach (var n in new[] { 20, 19, 18, 17, 16, 15 }) { g.Throw(T(n)); }
        g.Throw(D(25)); g.Throw(S(25));
        Assert.True(g.Snapshot.IsFinished);
    }

    [Fact] public void AroundTheClockAdvancesOnlyOnTarget()
    {
        var g = Solo(new GameSettings(GameKind.AroundTheClock, MaxRounds: 30));
        g.Throw(S(1)); g.Throw(S(5)); g.Throw(T(2));
        Assert.Equal(2, g.Snapshot.Players[0].Score);
        Assert.Equal(3, g.CurrentTarget!.Segment);
    }

    [Fact] public void ShanghaiScoresTargetAndShanghaiWins()
    {
        var g = Solo(new GameSettings(GameKind.Shanghai, MaxRounds: 7));
        g.Throw(S(1)); g.Throw(T(1)); g.Throw(S(20));
        Assert.Equal(4, g.Snapshot.Players[0].Score);
        g.Throw(S(2)); g.Throw(D(2)); g.Throw(T(2));
        Assert.True(g.Snapshot.IsFinished);
    }

    [Fact] public void HalveItHalvesOnMiss()
    {
        var g = Solo(new GameSettings(GameKind.HalveIt));
        g.Throw(T(15)); g.Throw(M); g.Throw(M);          // round 1 target 15: +45
        Assert.Equal(45, g.Snapshot.Players[0].Score);
        g.Throw(S(20)); g.Throw(M); g.Throw(M);          // round 2 target 16 missed
        Assert.Equal(22, g.Snapshot.Players[0].Score);
        g.Throw(D(5)); g.Throw(M); g.Throw(M);           // round 3 any double: +10
        Assert.Equal(32, g.Snapshot.Players[0].Score);
    }

    [Fact] public void Bob27AddsAndSubtracts()
    {
        var g = Solo(new GameSettings(GameKind.Bob27));
        g.Throw(D(1)); g.Throw(M); g.Throw(M);           // +2 => 29
        Assert.Equal(29, g.Snapshot.Players[0].Score);
        g.Throw(M); g.Throw(M); g.Throw(M);              // -4 => 25
        Assert.Equal(25, g.Snapshot.Players[0].Score);
        Assert.Equal("D3", g.CurrentTarget!.Label);
    }

    [Fact] public void Bob27EliminationEndsSoloGame()
    {
        var g = Solo(new GameSettings(GameKind.Bob27));
        for (int r = 0; r < 21 && !g.Snapshot.IsFinished; r++) { g.Throw(M); g.Throw(M); g.Throw(M); }
        Assert.True(g.Snapshot.IsFinished);
        Assert.True(g.Snapshot.Players[0].Eliminated);
    }

    [Fact] public void CountUpWinsAtTarget()
    {
        var g = Solo(new GameSettings(GameKind.CountUp, 100, 99));
        g.Throw(T(20)); g.Throw(T(20));
        Assert.True(g.Snapshot.IsFinished);
    }

    [Fact] public void HighScoreEndsAfterRounds()
    {
        var g = Solo(new GameSettings(GameKind.HighScore, MaxRounds: 2));
        for (int i = 0; i < 6; i++) g.Throw(S(20));
        Assert.True(g.Snapshot.IsFinished);
        Assert.Equal(120, g.Snapshot.Players[0].Score);
    }

    [Theory]
    [InlineData(170, 3)]
    [InlineData(100, 2)]
    [InlineData(40, 1)]
    [InlineData(50, 1)]
    public void CheckoutFindsRoutes(int remaining, int darts)
    {
        var route = Checkout.Suggest(remaining, 3, OutRule.Double);
        Assert.NotNull(route);
        Assert.Equal(darts, route!.Count);
        Assert.Equal(remaining, route.Sum(d => d.Score));
        Assert.Equal(2, route[^1].Multiplier);
    }

    [Fact] public void NoCheckoutForBogeyNumbers() => Assert.Null(Checkout.Suggest(169, 3, OutRule.Double));

    [Fact] public void BotPlaysEveryModeWithoutErrors()
    {
        var bot = new DartBot(42);
        foreach (var kind in Enum.GetValues<GameKind>())
        {
            var g = new DartGame(new GameSettings(kind, kind == GameKind.CountUp ? 300 : 301, 10), [PlayerInfo.Create("Bot", true), PlayerInfo.Create("Bot2", true)]);
            for (int i = 0; i < 3000 && !g.Snapshot.IsFinished; i++) g.Throw(bot.ThrowFor(g, 12));
            if (GameRules.IsRoundLimited(kind)) Assert.True(g.Snapshot.IsFinished, kind.ToString());
        }
    }

    [Fact] public void AllowsUpToEightPlayers()
    {
        var eight = Enumerable.Range(1, 8).Select(i => PlayerInfo.Create($"P{i}")).ToList();
        var g = new DartGame(new GameSettings(GameKind.Cricket), eight);
        for (int i = 0; i < 24; i++) g.Throw(DartHit.Miss);
        Assert.Equal(2, g.Snapshot.Round);
        Assert.Throws<ArgumentException>(() => new DartGame(new GameSettings(GameKind.X01), Enumerable.Range(1, 9).Select(i => PlayerInfo.Create($"P{i}"))));
    }
}
