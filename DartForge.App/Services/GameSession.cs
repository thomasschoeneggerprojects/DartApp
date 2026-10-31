using DartForge.Core.Engine;
using DartForge.Core.Models;
namespace DartForge.App.Services;

/// <summary>One seat in the setup screen: a human or a DartBot with its own level.</summary>
public sealed class PlayerSlot
{
    public string Name { get; set; } = "";
    public bool IsBot { get; set; }
    public int Level { get; set; } = 10;
}

public sealed class GameSession
{
    private GameSettings? _lastSettings;
    private List<PlayerSlot> _lastSlots = new();
    private readonly Dictionary<Guid, int> _botLevels = new();
    public DartGame? Current { get; private set; }
    public bool Saved { get; private set; }

    /// <summary>Legacy overload: one human, optionally against one DartBot.</summary>
    public void Start(GameSettings config, string player, bool bot, int botLevel = 10)
    {
        var slots = new List<PlayerSlot> { new() { Name = player } };
        if (bot) slots.Add(new PlayerSlot { Name = "DartBot", IsBot = true, Level = botLevel });
        Start(config, slots);
    }

    /// <summary>Starts a game with 1 to <see cref="GameRules.MaxPlayers"/> players in the given throwing order.</summary>
    public void Start(GameSettings config, IEnumerable<PlayerSlot> slots)
    {
        var list = slots.Select(s => new PlayerSlot { Name = s.Name, IsBot = s.IsBot, Level = Math.Clamp(s.Level, 1, 20) }).ToList();
        if (list.Count is < 1 or > GameRules.MaxPlayers) throw new ArgumentException($"Choose 1 to {GameRules.MaxPlayers} players.");
        _lastSettings = config; _lastSlots = list;
        _botLevels.Clear();
        var humans = 0; var bots = 0;
        var players = new List<PlayerInfo>();
        foreach (var s in list)
        {
            var fallback = s.IsBot ? $"DartBot {++bots}" : $"Player {++humans}";
            var name = string.IsNullOrWhiteSpace(s.Name) ? fallback : s.Name.Trim();
            if (s.IsBot) name = $"{name} · Lv{s.Level}";
            var info = PlayerInfo.Create(name, s.IsBot);
            if (s.IsBot) _botLevels[info.Id] = s.Level;
            players.Add(info);
        }
        Current = new DartGame(config, players); Saved = false;
    }

    /// <summary>Starts a fresh game with the same settings and players.</summary>
    public void Rematch()
    {
        if (_lastSettings is null) return;
        Start(_lastSettings, _lastSlots.ToList());
    }

    /// <summary>DartBot level of a player (10 if unknown).</summary>
    public int LevelOf(Guid playerId) => _botLevels.TryGetValue(playerId, out var l) ? l : 10;

    public void MarkSaved() => Saved = true;
}
