using DartForge.Core.Models;
namespace DartForge.Core.Engine;
public sealed class DartBot
{
    private readonly Random _random;
    public DartBot(int? seed = null) => _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    public DartHit Throw(int level, GameKind mode, int remaining = 501, int target = 1)
    {
        level = Math.Clamp(level, 1, 20);
        var intended = mode switch
        {
            GameKind.AroundTheClock => Math.Clamp(target,1,20),
            GameKind.Cricket or GameKind.CutThroatCricket or GameKind.NoScoreCricket => 20,
            _ when remaining <= 40 && remaining % 2 == 0 && remaining > 0 => remaining / 2,
            _ => 20
        };
        double hitChance = 0.18 + level * .037;
        bool accurate = _random.NextDouble() < hitChance;
        int segment = accurate ? intended : _random.Next(1, 21);
        int multiplier = accurate && mode == GameKind.X01 && remaining <= 40 && remaining % 2 == 0 ? 2 :
            accurate && _random.NextDouble() < (0.08 + level * .028) ? 3 : 1;
        return new DartHit(segment, multiplier);
    }
}
