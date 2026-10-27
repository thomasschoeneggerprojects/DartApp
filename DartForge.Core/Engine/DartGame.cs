using DartForge.Core.Models;
namespace DartForge.Core.Engine;
/// <summary>Pure, independent scoring engine with visit-based undo.</summary>
public sealed class DartGame
{
    private sealed class State
    {
        public required PlayerInfo Player { get; init; }
        public int Score, Legs, Sets, DartsThrown, Visits, TotalScored;
        public bool Opened;
        public Dictionary<int, int> Marks { get; } = new();
        public State Clone()
        {
            var copy = new State { Player = Player, Score = Score, Legs = Legs, Sets = Sets, DartsThrown = DartsThrown, Visits = Visits, TotalScored = TotalScored, Opened = Opened };
            foreach (var (k,v) in Marks) copy.Marks[k] = v;
            return copy;
        }
    }
    private sealed record Checkpoint(List<State> Players, int Active, int Round, int Darts, bool Finished, Guid? Winner, List<Visit> Visits, List<DartHit> Pending);
    private readonly List<State> _players;
    private readonly Stack<Checkpoint> _undo = new();
    private readonly Stack<Checkpoint> _redo = new();
    private readonly List<Visit> _visits = new();
    private readonly List<DartHit> _pending = new();
    private int _active, _round = 1;
    private bool _finished;
    private Guid? _winner;
    public GameSettings Settings { get; }
    public IReadOnlyList<DartHit> Pending => _pending.AsReadOnly();
    public GameSnapshot Snapshot => new(Settings, _players.Select(p => new PlayerSnapshot(p.Player.Id, p.Player.Name, p.Score, p.Legs, p.Sets, p.DartsThrown, p.Visits, p.TotalScored, p.Marks.Values.Sum(), p.Player.IsBot)).ToArray(), _active, _round, _pending.Count, _finished, _winner, _visits.ToArray());
    public DartGame(GameSettings settings, IEnumerable<PlayerInfo> players)
    {
        Settings = settings;
        var list = players.ToList();
        if (list.Count is < 1 or > 16 || list.Select(p => p.Id).Distinct().Count() != list.Count) throw new ArgumentException("Provide 1 to 16 unique players.");
        if (settings.LegsToWin < 1 || settings.SetsToWin < 1 || settings.MaxRounds < 1) throw new ArgumentException("Invalid match configuration.");
        _players = list.Select(p => new State { Player = p, Score = InitialScore(), Opened = settings.In == InRule.Straight }).ToList();
    }
    private int InitialScore() => Settings.Kind == GameKind.X01 ? Settings.StartScore : Settings.Kind == GameKind.Bob27 ? 27 : 0;
    private Checkpoint Capture() => new(_players.Select(p=>p.Clone()).ToList(), _active, _round, _pending.Count, _finished, _winner, _visits.ToList(), _pending.ToList());
    private void Restore(Checkpoint c)
    {
        _players.Clear(); _players.AddRange(c.Players.Select(p=>p.Clone())); _active = c.Active; _round = c.Round; _finished = c.Finished; _winner = c.Winner;
        _pending.Clear(); _pending.AddRange(c.Pending); _visits.Clear(); _visits.AddRange(c.Visits);
    }
    public GameEvent Throw(DartHit dart)
    {
        if (!dart.IsValid) throw new ArgumentException("Invalid dart.", nameof(dart));
        if (_finished) throw new InvalidOperationException("Game is finished.");
        if (_pending.Count == 3) throw new InvalidOperationException("Visit full.");
        _undo.Push(Capture()); _redo.Clear();
        _pending.Add(dart);
        var eventResult = EvaluatePending();
        if (_pending.Count >= 3 || eventResult.WinnerId != null || IsBust()) FinishVisit(eventResult.WinnerId != null || IsBust());
        return eventResult;
    }
    public GameEvent EnterVisitTotal(int score)
    {
        if (Settings.Kind != GameKind.X01 || Settings.In != InRule.Straight || Settings.Out != OutRule.Straight) throw new InvalidOperationException("Use dart-by-dart input for this ruleset.");
        if (score < 0 || score > 180 || _finished || _pending.Count != 0) throw new ArgumentException("Invalid visit score.");
        _undo.Push(Capture()); _redo.Clear();
        var player = _players[_active];
        var bust = score > player.Score;
        if (!bust) player.Score -= score;
        player.TotalScored += bust ? 0 : score;
        player.DartsThrown += 3; player.Visits++;
        _visits.Add(new Visit(player.Player.Id, _round, Array.Empty<DartHit>(), score, bust, DateTimeOffset.UtcNow));
        if (player.Score == 0) AwardLeg(); else Advance();
        return new GameEvent(bust ? "Bust" : "Visit saved", _winner);
    }
    private int StartOfVisitScore => _players[_active].Score + (_pending.Count == 0 ? 0 : _pending.Take(_pending.Count-1).Sum(d => d.Score));
    private bool IsBust()
    {
        if (Settings.Kind != GameKind.X01) return false;
        var p = _players[_active];
        var remaining = p.Score;
        return remaining < 0 || (Settings.Out == OutRule.Double && remaining == 1) ||
               (remaining == 0 && !IsAllowedFinish(_pending[^1]));
    }
    private bool IsAllowedFinish(DartHit hit) => Settings.Out switch { OutRule.Double => hit.Multiplier == 2, OutRule.Master => hit.Multiplier >= 2, _ => true };
    private GameEvent EvaluatePending()
    {
        var p = _players[_active]; var hit = _pending[^1];
        switch (Settings.Kind)
        {
            case GameKind.X01:
                if (!p.Opened)
                {
                    if (Settings.In == InRule.Double && hit.Multiplier != 2 || Settings.In == InRule.Master && hit.Multiplier < 2) break;
                    p.Opened = true;
                }
                p.Score -= hit.Score;
                if (!IsBust() && p.Score == 0) return new GameEvent("Checkout!", p.Player.Id);
                break;
            case GameKind.Cricket:
            case GameKind.CutThroatCricket:
            case GameKind.NoScoreCricket:
                ApplyCricket(p, hit); break;
            case GameKind.AroundTheClock:
                if (hit.Segment == p.Score + 1) p.Score++;
                if (p.Score >= 20) return new GameEvent("Around the clock complete!", p.Player.Id);
                break;
            case GameKind.Shanghai:
                if (hit.Segment == Math.Min(_round,20)) p.Score += hit.Score;
                break;
            case GameKind.HalveIt:
                if (hit.Segment == HalveTarget(_round)) p.Score += hit.Score;
                break;
            case GameKind.Bob27:
                if (hit.Segment == _round && hit.Multiplier == 2) p.Score += 2 * _round;
                break;
            default: p.Score += hit.Score; break;
        }
        return new GameEvent("Dart recorded");
    }
    private static int HalveTarget(int round) => new[] { 20, 19, 18, 17, 16, 15, 25 }[Math.Min(round-1,6)];
    private void ApplyCricket(State player, DartHit hit)
    {
        if (hit.Segment is < 15 or > 20 && hit.Segment != 25) return;
        int current = player.Marks.GetValueOrDefault(hit.Segment);
        int added = Math.Min(3-current, hit.Multiplier);
        player.Marks[hit.Segment] = Math.Min(3, current + hit.Multiplier);
        int excess = hit.Multiplier - Math.Max(0,added);
        bool othersOpen = _players.Any(p=>p != player && p.Marks.GetValueOrDefault(hit.Segment) < 3);
        if (othersOpen && excess > 0 && Settings.Kind != GameKind.NoScoreCricket)
        {
            if (Settings.Kind == GameKind.CutThroatCricket)
                foreach(var other in _players.Where(p=>p != player && p.Marks.GetValueOrDefault(hit.Segment)<3)) other.Score += hit.Segment * excess;
            else player.Score += hit.Segment * excess;
        }
    }
    private bool CricketWon(State player)
    {
        int[] targets = [15,16,17,18,19,20,25];
        if (!targets.All(t=>player.Marks.GetValueOrDefault(t) >= 3)) return false;
        return Settings.Kind == GameKind.CutThroatCricket ? _players.All(p => p == player || player.Score <= p.Score) : _players.All(p => p == player || player.Score >= p.Score);
    }
    private void FinishVisit(bool terminal)
    {
        var p = _players[_active];
        var bust = Settings.Kind == GameKind.X01 && IsBust();
        // Restore original X01 score on bust, including if the player has just opened.
        if (bust)
        {
            var checkpoint = _undo.ToArray().FirstOrDefault(c => c.Darts == 0 && c.Active == _active);
            if (checkpoint != null)
            {
                var original = checkpoint.Players[_active]; p.Score = original.Score; p.Opened = original.Opened;
            }
        }
        var score = _pending.Sum(d=>d.Score);
        p.DartsThrown += _pending.Count; p.Visits++;
        if (!bust && Settings.Kind == GameKind.X01) p.TotalScored += score;
        _visits.Add(new Visit(p.Player.Id, _round, _pending.ToArray(), score, bust, DateTimeOffset.UtcNow));
        _pending.Clear();
        if (terminal && !bust && Settings.Kind == GameKind.X01 && p.Score == 0) { AwardLeg(); return; }
        if (Settings.Kind is GameKind.Cricket or GameKind.CutThroatCricket or GameKind.NoScoreCricket && CricketWon(p)) { AwardLeg(); return; }
        if (Settings.Kind == GameKind.AroundTheClock && p.Score >= 20) { AwardLeg(); return; }
        if (Settings.Kind == GameKind.Bob27 && p.Score <= 0) { _finished = true; return; }
        Advance();
    }
    private void Advance()
    {
        _active = (_active + 1) % _players.Count;
        if (_active == 0) _round++;
        if (_round > Settings.MaxRounds && Settings.Kind != GameKind.X01 && Settings.Kind is not GameKind.Cricket and not GameKind.CutThroatCricket and not GameKind.NoScoreCricket)
        {
            var winner = Settings.Kind == GameKind.Bob27 ? _players.MaxBy(p=>p.Score) : _players.MaxBy(p=>p.Score);
            if (winner != null) AwardLegTo(winner);
        }
    }
    private void AwardLeg() => AwardLegTo(_players[_active]);
    private void AwardLegTo(State p)
    {
        p.Legs++;
        if (p.Legs >= Settings.LegsToWin)
        {
            p.Sets++; foreach (var x in _players) x.Legs = 0;
        }
        if (p.Sets >= Settings.SetsToWin) { _finished = true; _winner = p.Player.Id; return; }
        foreach (var x in _players) { x.Score = InitialScore(); x.Opened = Settings.In == InRule.Straight; x.Marks.Clear(); }
        _active = 0; _round = 1;
    }
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Capture()); Restore(_undo.Pop()); return true;
    }
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(Capture()); Restore(_redo.Pop()); return true;
    }
}
