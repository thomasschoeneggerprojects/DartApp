using DartForge.Core.Engine;
using DartForge.Core.Models;
namespace DartForge.App.Services;
public sealed class GameSession
{
    public DartGame? Current { get; private set; }
    public bool Saved { get; private set; }
    public void Start(GameSettings config, string player, bool bot, int botLevel = 10)
    {
        var players = new List<PlayerInfo> { PlayerInfo.Create(string.IsNullOrWhiteSpace(player) ? "Player 1" : player.Trim()) };
        if (bot) players.Add(PlayerInfo.Create($"DartBot (Lv {botLevel})", true));
        Current = new DartGame(config, players); Saved = false;
    }
    public void MarkSaved() => Saved = true;
}
