using DartForge.Core.Models;
namespace DartForge.Core.Engine;

/// <summary>Suggests X01 finishing routes.</summary>
public static class Checkout
{
    private static readonly DartHit[] SetupOrder = BuildSetupOrder();
    private static readonly DartHit[] DoubleFinishOrder = BuildDoubleFinishOrder();
    private static readonly DartHit[] MasterFinishOrder = [.. DoubleFinishOrder, .. Enumerable.Range(1, 20).Reverse().Select(n => new DartHit(n, 3))];
    private static readonly DartHit[] StraightFinishOrder = [.. Enumerable.Range(1, 20).Reverse().Select(n => new DartHit(n, 1)), new(25, 1), .. MasterFinishOrder];

    private static DartHit[] BuildSetupOrder()
    {
        var list = new List<DartHit>();
        list.AddRange(Enumerable.Range(10, 11).Reverse().Select(n => new DartHit(n, 3)));
        list.Add(new DartHit(25, 2)); list.Add(new DartHit(25, 1));
        list.AddRange(Enumerable.Range(1, 20).Reverse().Select(n => new DartHit(n, 1)));
        list.AddRange(Enumerable.Range(1, 9).Reverse().Select(n => new DartHit(n, 3)));
        list.AddRange(Enumerable.Range(1, 20).Reverse().Select(n => new DartHit(n, 2)));
        return list.ToArray();
    }

    private static DartHit[] BuildDoubleFinishOrder()
    {
        int[] preferred = [20, 16, 18, 12, 10, 8, 14, 4, 6, 2, 19, 17, 15, 13, 11, 9, 7, 5, 3, 1];
        return [.. preferred.Select(n => new DartHit(n, 2)), new DartHit(25, 2)];
    }

    /// <summary>Returns a finishing route for <paramref name="remaining"/> using at most <paramref name="dartsLeft"/> darts, or null when none exists.</summary>
    public static IReadOnlyList<DartHit>? Suggest(int remaining, int dartsLeft, OutRule rule)
    {
        if (remaining <= 0 || dartsLeft <= 0 || remaining > 180) return null;
        var finishes = rule switch { OutRule.Double => DoubleFinishOrder, OutRule.Master => MasterFinishOrder, _ => StraightFinishOrder };
        foreach (var f in finishes) if (f.Score == remaining) return [f];
        if (dartsLeft >= 2)
            foreach (var a in SetupOrder)
            {
                var rest = remaining - a.Score; if (rest <= 0) continue;
                foreach (var f in finishes) if (f.Score == rest) return [a, f];
            }
        if (dartsLeft >= 3)
            foreach (var a in SetupOrder)
            foreach (var b in SetupOrder)
            {
                var rest = remaining - a.Score - b.Score; if (rest <= 0) continue;
                foreach (var f in finishes) if (f.Score == rest) return [a, b, f];
            }
        return null;
    }
}
