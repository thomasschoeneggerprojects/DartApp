using DartForge.Core.Engine;
using DartForge.Core.Models;
using DartForge.Localization;
using Microsoft.AspNetCore.Components;
namespace DartForge.App.Components.Play;

/// <summary>Shared parameters and helpers for the game-specific boards.</summary>
public abstract class GameBoardBase : LocalizedComponentBase
{
    [Parameter, EditorRequired] public DartGame Game { get; set; } = default!;
    [Parameter, EditorRequired] public GameSnapshot Snap { get; set; } = default!;
    /// <summary>True when a human player is at the oche and may enter darts.</summary>
    [Parameter] public bool CanInput { get; set; }
    [Parameter] public EventCallback<DartHit> OnThrow { get; set; }

    protected PlayerSnapshot Active => Snap.Players[Snap.ActivePlayer];
    protected bool IsActive(int index) => index == Snap.ActivePlayer && !Snap.IsFinished;
    protected int DartsLeft => 3 - Snap.DartsInVisit;
    protected IReadOnlyList<DartHit> PendingDarts => Game.Pending;
    protected int TotalRounds => GameRules.TotalRounds(Snap.Settings);
    protected Visit? VisitOf(Guid playerId, int round) => Snap.Visits.LastOrDefault(v => v.PlayerId == playerId && v.Round == round);
    protected IEnumerable<Visit> VisitsOf(Guid playerId) => Snap.Visits.Where(v => v.PlayerId == playerId);
    protected Task Throw(DartHit hit) => OnThrow.InvokeAsync(hit);
    /// <summary>Three-dart average, formatted for the UI culture (45.8 / 45,8). Calculation unchanged.</summary>
    protected string Avg(PlayerSnapshot p) => Loc.FormatNumber(p.DartsThrown == 0 ? 0 : p.TotalScored * 3.0 / p.DartsThrown, 1);
    protected string Role(PlayerSnapshot p, int index) => (p.IsBot ? L("Players_BotPlaceholder") : L("Players_DefaultName", index + 1)).ToUpper(Loc.CurrentCulture);
    /// <summary>More than four players: boards switch to a denser layout.</summary>
    protected bool Compact => Snap.Players.Count > 4;
    protected string CardsClass => Snap.Players.Count switch { <= 2 => "scorecards", <= 4 => "scorecards four", _ => "scorecards compact" };
    protected static string Signed(int v) => v > 0 ? $"+{v}" : v.ToString();
}
