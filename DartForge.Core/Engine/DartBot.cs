using DartForge.Core.Models;
namespace DartForge.Core.Engine;
public sealed class DartBot
{
    private readonly Random _random;
    public DartBot(int? seed = null) => _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;

    /// <summary>Throws one dart for the active player of <paramref name="game"/>, aiming at the game's suggested target.</summary>
    public DartHit ThrowFor(DartGame game, int level) => ThrowAt(level, game.SuggestedAim());

    /// <summary>Simulates a dart aimed at <paramref name="aim"/>. Level 1 (beginner) to 20 (pro).</summary>
    public DartHit ThrowAt(int level, DartHit aim)
    {
        level = Math.Clamp(level, 1, 20);
        if (aim.Segment == 25)
        {
            if (_random.NextDouble() < 0.10 + level * 0.03)
                return new DartHit(25, _random.NextDouble() < 0.25 + level * 0.02 ? 2 : 1);
            return new DartHit(_random.Next(1, 21), 1);
        }
        if (aim.Segment is < 1 or > 20) return DartHit.Miss;
        int segment = aim.Segment;
        if (_random.NextDouble() >= 0.35 + level * 0.03)
        {
            if (_random.NextDouble() < 0.05) return DartHit.Miss;
            var idx = Array.IndexOf(GameRules.BoardOrder, aim.Segment);
            var step = _random.Next(2) == 0 ? -1 : 1;
            segment = GameRules.BoardOrder[(idx + step + GameRules.BoardOrder.Length) % GameRules.BoardOrder.Length];
        }
        int multiplier = 1;
        if (segment == aim.Segment && aim.Multiplier == 3 && _random.NextDouble() < 0.06 + level * 0.025) multiplier = 3;
        else if (segment == aim.Segment && aim.Multiplier == 2)
        {
            if (_random.NextDouble() < 0.08 + level * 0.025) multiplier = 2;
            else if (_random.NextDouble() < 0.5) return DartHit.Miss; // landed outside the double ring
        }
        else if (_random.NextDouble() < 0.03) multiplier = 3;
        return new DartHit(segment, multiplier);
    }

    /// <summary>Legacy API kept for compatibility.</summary>
    public DartHit Throw(int level, GameKind mode, int remaining = 501, int target = 1)
    {
        var aim = mode switch
        {
            GameKind.AroundTheClock => new DartHit(Math.Clamp(target, 1, 20), 1),
            GameKind.X01 when remaining <= 40 && remaining % 2 == 0 && remaining > 0 => new DartHit(remaining / 2, 2),
            _ => new DartHit(20, 3)
        };
        return ThrowAt(level, aim);
    }
}
