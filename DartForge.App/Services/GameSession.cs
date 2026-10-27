using DartForge.Core.Engine;
using DartForge.Core.Models;
namespace DartForge.App.Services;
public enum Opponent { DartBot, SecondPlayer, Solo }
public sealed class GameSession
{
    private GameSettings? _lastSettings;
    private List<(string Name, bool IsBot)> _lastPlayers = new();
    public DartGame? Current { get; private set; }
    public bool Saved { get; private set; }
    public int BotLevel { get; private set; } = 10;

    public void Start(GameSettings config, string player, bool bot, int botLevel = 10) =>
        Start(config, player, bot ? Opponent.DartBot : Opponent.Solo, null, botLevel);

    public void Start(GameSettings config, string player, Opponent opponent, string? secondPlayer, int botLevel = 10)
    {
        var players = new List<(string, bool)> { (string.IsNullOrWhiteSpace(player) ? "Player 1" : player.Trim(), false) };
        if (opponent == Opponent.DartBot) players.Add(($"DartBot (Lv {botLevel})", true));
        if (opponent == Opponent.SecondPlayer) players.Add((string.IsNullOrWhiteSpace(secondPlayer) ? "Player 2" : secondPlayer.Trim(), false));
        BotLevel = botLevel;
        Begin(config, players);
    }

    /// <summary>Starts a fresh game with the same settings and players.</summary>
    public void Rematch()
    {
        if (_lastSettings is null) return;
        Begin(_lastSettings, _lastPlayers.ToList());
    }

    private void Begin(GameSettings config, List<(string Name, bool IsBot)> players)
    {
        _lastSettings = config; _lastPlayers = players;
        Current = new DartGame(config, players.Select(p => PlayerInfo.Create(p.Name, p.IsBot))); Saved = false;
    }

    public void MarkSaved() => Saved = true;
}
