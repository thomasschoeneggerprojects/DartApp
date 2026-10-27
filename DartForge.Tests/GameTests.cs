using DartForge.Core.Engine;
using DartForge.Core.Models;
using Xunit;
namespace DartForge.Tests;
public sealed class GameTests
{
    [Fact] public void DoubleOutWonOnDouble()
    {
        var p=PlayerInfo.Create("Alice"); var g=new DartGame(new GameSettings(GameKind.X01,40,Out:OutRule.Double),[p]);
        g.Throw(new DartHit(20,2)); Assert.True(g.Snapshot.IsFinished);Assert.Equal(p.Id,g.Snapshot.WinnerId);
    }
    [Fact] public void BustRestoresOriginalScore()
    {
        var g=new DartGame(new GameSettings(GameKind.X01,40,Out:OutRule.Double),[PlayerInfo.Create("Alice")]);
        g.Throw(new DartHit(20,3)); Assert.Equal(40,g.Snapshot.Players[0].Score);
    }
    [Fact] public void UndoRestoresState()
    {
        var g=new DartGame(new GameSettings(GameKind.X01,501),[PlayerInfo.Create("Alice")]);
        g.Throw(new DartHit(20,3));Assert.Equal(441,g.Snapshot.Players[0].Score);
        Assert.True(g.Undo());Assert.Equal(501,g.Snapshot.Players[0].Score);
    }
    [Fact] public void OnlyValidHitsAllowed()
    {
        var g=new DartGame(new GameSettings(GameKind.X01),[PlayerInfo.Create("Alice")]);
        Assert.Throws<ArgumentException>(()=>g.Throw(new DartHit(25,3)));
    }
}
