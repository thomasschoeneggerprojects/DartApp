using DartForge.Core.Models;
namespace DartForge.Core.Engine;
/// <summary>Pure, independent scoring engine with dart-level undo.</summary>
public sealed class DartGame
{
    private sealed class State
    {
        public required PlayerInfo Player { get; init; }
        public int Score, Legs, Sets, DartsThrown, Visits, TotalScored;
        public bool Opened, Eliminated;
        public Dictionary<int, int> Marks { get; } = new();
        public State Clone()
        {
            var copy = new State { Player = Player, Score = Score, Legs = Legs, Sets = Sets, DartsThrown = DartsThrown, Visits = Visits, TotalScored = TotalScored, Opened = Opened, Eliminated = Eliminated };
            foreach (var (k, v) in Marks) copy.Marks[k] = v;
            return copy;
        }
    }
    private sealed record Checkpoint(List<State> Players, int Active, int Round, bool Finished, Guid? Winner, List<Visit> Visits, List<DartHit> Pending, int VisitStartScore, bool VisitStartOpened);
    private readonly List<State> _players;
    private readonly Stack<Checkpoint> _undo = new();
    private readonly Stack<Checkpoint> _redo = new();
    private readonly List<Visit> _visits = new();
    private readonly List<DartHit> _pending = new();
    private int _active, _round = 1, _visitStartScore;
    private bool _finished, _visitStartOpened;
    private Guid? _winner;
    public GameSettings Settings { get; }
    public IReadOnlyList<DartHit> Pending => _pending.AsReadOnly();
    public int TotalRounds => GameRules.TotalRounds(Settings);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public GameSnapshot Snapshot => new(Settings,
        _players.Select(p => new PlayerSnapshot(p.Player.Id, p.Player.Name, p.Score, p.Legs, p.Sets, p.DartsThrown, p.Visits, p.TotalScored, p.Marks.Values.Sum(), p.Player.IsBot,
            new Dictionary<int, int>(p.Marks), p.Eliminated, p.Opened)).ToArray(),
        _active, _round, _pending.Count, _finished, _winner, _visits.ToArray());

    public DartGame(GameSettings settings, IEnumerable<PlayerInfo> players)
    {
        Settings = settings;
        var list = players.ToList();
        if (list.Count < 1 || list.Count > GameRules.MaxPlayers || list.Select(p => p.Id).Distinct().Count() != list.Count) throw new ArgumentException($"Provide 1 to {GameRules.MaxPlayers} unique players.");
        if (settings.LegsToWin < 1 || settings.SetsToWin < 1 || settings.MaxRounds < 1) throw new ArgumentException("Invalid match configuration.");
        if (settings.Kind is GameKind.X01 or GameKind.CountUp && settings.StartScore < 2) throw new ArgumentException("Invalid start/target score.");
        _players = list.Select(p => new State { Player = p, Score = InitialScore(), Opened = settings.In == InRule.Straight }).ToList();
    }

    private int InitialScore() => Settings.Kind == GameKind.X01 ? Settings.StartScore : Settings.Kind == GameKind.Bob27 ? 27 : 0;

    /// <summary>The target of the active player for target-based games (Around the Clock, Shanghai, Halve It, Bob's 27), otherwise null.</summary>
    public TargetInfo? CurrentTarget => TargetFor(_active);

    public TargetInfo? TargetFor(int playerIndex)
    {
        var p = _players[Math.Clamp(playerIndex, 0, _players.Count - 1)];
        return Settings.Kind switch
        {
            GameKind.AroundTheClock => new TargetInfo($"{Math.Min(20, p.Score + 1)}", Math.Min(20, p.Score + 1), null),
            GameKind.Shanghai => GameRules.ShanghaiTarget(_round),
            GameKind.HalveIt => GameRules.HalveItTarget(_round),
            GameKind.Bob27 => GameRules.Bob27Target(_round),
            _ => null
        };
    }

    private Checkpoint Capture() => new(_players.Select(p => p.Clone()).ToList(), _active, _round, _finished, _winner, _visits.ToList(), _pending.ToList(), _visitStartScore, _visitStartOpened);
    private void Restore(Checkpoint c)
    {
        _players.Clear(); _players.AddRange(c.Players.Select(p => p.Clone())); _active = c.Active; _round = c.Round; _finished = c.Finished; _winner = c.Winner;
        _pending.Clear(); _pending.AddRange(c.Pending); _visits.Clear(); _visits.AddRange(c.Visits);
        _visitStartScore = c.VisitStartScore; _visitStartOpened = c.VisitStartOpened;
    }

    public GameEvent Throw(DartHit dart)
    {
        if (!dart.IsValid) throw new ArgumentException("Invalid dart.", nameof(dart));
        if (_finished) throw new InvalidOperationException("Game is finished.");
        if (_pending.Count == 3) throw new InvalidOperationException("Visit full.");
        _undo.Push(Capture()); _redo.Clear();
        var p = _players[_active];
        if (_pending.Count == 0) { _visitStartScore = p.Score; _visitStartOpened = p.Opened; }
        _pending.Add(dart);
        var result = EvaluatePending();
        var bust = IsBust();
        if (_pending.Count >= 3 || result.WinnerId != null || bust) return FinishVisit(result, bust);
        return result;
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

    private bool IsBust()
    {
        if (Settings.Kind != GameKind.X01 || _pending.Count == 0) return false;
        var remaining = _players[_active].Score;
        return remaining < 0 || (Settings.Out != OutRule.Straight && remaining == 1) ||
               (remaining == 0 && !IsAllowedFinish(_pending[^1]));
    }
    private bool IsAllowedFinish(DartHit hit) => Settings.Out switch { OutRule.Double => hit.Multiplier == 2, OutRule.Master => hit.Multiplier >= 2, _ => true };

    private GameEvent EvaluatePending()
    {
        var p = _players[_active]; var hit = _pending[^1];
        var target = CurrentTarget;
        switch (Settings.Kind)
        {
            case GameKind.X01:
                if (!p.Opened)
                {
                    if (hit.Segment == 0 || Settings.In == InRule.Double && hit.Multiplier != 2 || Settings.In == InRule.Master && hit.Multiplier < 2) return new GameEvent("Not opened yet");
                    p.Opened = true;
                }
                p.Score -= hit.Score;
                if (!IsBust() && p.Score == 0) return new GameEvent("Checkout!", p.Player.Id);
                break;
            case GameKind.Cricket:
            case GameKind.CutThroatCricket:
            case GameKind.NoScoreCricket:
                ApplyCricket(p, hit);
                if (CricketWon(p)) return new GameEvent("All closed — game shot!", p.Player.Id);
                break;
            case GameKind.AroundTheClock:
                if (target!.Matches(hit)) p.Score++;
                if (p.Score >= 20) return new GameEvent("Around the clock complete!", p.Player.Id);
                break;
            case GameKind.Shanghai:
                if (target!.Matches(hit)) p.Score += hit.Score;
                var onTarget = _pending.Where(target.Matches).Select(d => d.Multiplier).ToHashSet();
                if (onTarget.SetEquals([1, 2, 3])) return new GameEvent("SHANGHAI!", p.Player.Id);
                break;
            case GameKind.HalveIt:
            case GameKind.Bob27:
                if (target!.Matches(hit)) p.Score += hit.Score;
                break;
            case GameKind.CountUp:
                p.Score += hit.Score;
                if (p.Score >= Settings.StartScore) return new GameEvent("Target reached!", p.Player.Id);
                break;
            default: p.Score += hit.Score; break;
        }
        return new GameEvent(hit.Segment == 0 ? "Miss" : $"{hit.Label} recorded");
    }

    private void ApplyCricket(State player, DartHit hit)
    {
        if (hit.Segment is < 15 or > 20 && hit.Segment != 25) return;
        int current = player.Marks.GetValueOrDefault(hit.Segment);
        int added = Math.Min(3 - current, hit.Multiplier);
        player.Marks[hit.Segment] = Math.Min(3, current + hit.Multiplier);
        int excess = hit.Multiplier - Math.Max(0, added);
        var openOpponents = _players.Where(p => p != player && p.Marks.GetValueOrDefault(hit.Segment) < 3).ToList();
        if (openOpponents.Count == 0 || excess <= 0 || Settings.Kind == GameKind.NoScoreCricket) return;
        if (Settings.Kind == GameKind.CutThroatCricket)
            foreach (var other in openOpponents) other.Score += hit.Segment * excess;
        else player.Score += hit.Segment * excess;
    }

    private bool CricketWon(State player)
    {
        if (!GameRules.CricketNumbers.All(t => player.Marks.GetValueOrDefault(t) >= 3)) return false;
        return Settings.Kind switch
        {
            GameKind.CutThroatCricket => _players.All(p => p == player || player.Score <= p.Score),
            GameKind.NoScoreCricket => true,
            _ => _players.All(p => p == player || player.Score >= p.Score)
        };
    }

    private GameEvent FinishVisit(GameEvent result, bool bust)
    {
        var p = _players[_active];
        var target = CurrentTarget;
        var message = result.Message;
        if (bust) { p.Score = _visitStartScore; p.Opened = _visitStartOpened; message = "Bust!"; }
        if (Settings.Kind == GameKind.HalveIt && result.WinnerId == null && !_pending.Any(target!.Matches))
        {
            p.Score /= 2; message = "Target missed — score halved";
        }
        if (Settings.Kind == GameKind.Bob27 && !_pending.Any(target!.Matches))
        {
            p.Score -= GameRules.Bob27Value(_round); message = $"No double — minus {GameRules.Bob27Value(_round)}";
            if (p.Score <= 0) { p.Eliminated = true; message = $"{p.Player.Name} is out!"; }
        }
        var raw = _pending.Sum(d => d.Score);
        p.DartsThrown += _pending.Count; p.Visits++;
        if (!bust) p.TotalScored += raw;
        var visitScore = Settings.Kind == GameKind.X01 ? raw : p.Score - _visitStartScore;
        _visits.Add(new Visit(p.Player.Id, _round, _pending.ToArray(), visitScore, bust, DateTimeOffset.UtcNow));
        _pending.Clear();
        if (result.WinnerId != null) { AwardLeg(); return new GameEvent(message, _winner); }
        if (_players.All(x => x.Eliminated)) { AwardLegTo(Leader()); return new GameEvent(message, _winner); }
        Advance();
        return new GameEvent(message, _winner);
    }

    private State Leader() => Settings.Kind == GameKind.CutThroatCricket ? _players.MinBy(p => p.Score)! : _players.MaxBy(p => p.Score)!;

    private void Advance()
    {
        do
        {
            _active = (_active + 1) % _players.Count;
            if (_active == 0) _round++;
        } while (_players[_active].Eliminated);
        if (GameRules.IsRoundLimited(Settings.Kind) && _round > TotalRounds) AwardLegTo(Leader());
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
        foreach (var x in _players) { x.Score = InitialScore(); x.Opened = Settings.In == InRule.Straight; x.Eliminated = false; x.Marks.Clear(); }
        _active = 0; _round = 1;
    }

    /// <summary>A sensible aim point for the active player (used by DartBot).</summary>
    public DartHit SuggestedAim()
    {
        var p = _players[_active];
        var target = CurrentTarget;
        switch (Settings.Kind)
        {
            case GameKind.X01:
                if (!p.Opened) return Settings.In == InRule.Double ? new DartHit(20, 2) : new DartHit(20, 3);
                var route = Checkout.Suggest(p.Score, 3 - _pending.Count, Settings.Out);
                if (route is { Count: > 0 }) return route[0];
                if (p.Score > 60) return new DartHit(20, 3);
                foreach (var leave in new[] { 40, 32, 36, 24, 20, 16, 8 })
                    if (p.Score - leave is >= 1 and <= 20) return new DartHit(p.Score - leave, 1);
                return new DartHit(Math.Clamp(p.Score - 2, 1, 20), 1);
            case GameKind.Cricket:
            case GameKind.CutThroatCricket:
            case GameKind.NoScoreCricket:
                foreach (var n in GameRules.CricketNumbers)
                    if (p.Marks.GetValueOrDefault(n) < 3) return new DartHit(n, n == 25 ? 1 : 3);
                foreach (var n in GameRules.CricketNumbers)
                    if (_players.Any(o => o != p && o.Marks.GetValueOrDefault(n) < 3)) return new DartHit(n, n == 25 ? 1 : 3);
                return new DartHit(20, 3);
            case GameKind.AroundTheClock: return new DartHit(target!.Segment!.Value, 1);
            case GameKind.Shanghai: return new DartHit(target!.Segment!.Value, 3);
            case GameKind.Bob27: return new DartHit(target!.Segment!.Value, 2);
            case GameKind.HalveIt:
                if (target!.Segment == 25) return new DartHit(25, 1);
                return new DartHit(target.Segment ?? 20, target.Multiplier ?? 3);
            default: return new DartHit(20, 3);
        }
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
