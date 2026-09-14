namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// The confluence-assembly knobs (gh#730, ADR-0026 §3, R-4) — how issuance corroborates a fired signal against the
/// other timeframes and the levels near the entry. Bound from the <c>Confluence</c> configuration section and
/// consumed by the hosted trigger scan (<see cref="TriggerScanHost"/>), so a bad value must fail the host on start.
/// </summary>
/// <remarks>
/// <b>Tuning knobs, deliberately not constants.</b> ADR-0026 §3 makes <see cref="KTicks"/> and <see cref="FAtr"/>
/// options — the proximity band and the corroboration ladder are judgement calls that will want tuning against real
/// feeds (the same lesson gh#836 drew), and they are the first thing to revisit if confluence never fires or fires on
/// everything. An <b>empty</b> <see cref="TimeframeMinutes"/> is valid and inert: it assembles no supporting factors,
/// so every suggestion stays the degenerate N=1 "set of one" — the safe direction (it can never fabricate a
/// citation), and the exact behaviour of every suggestion before gh#730.
/// </remarks>
public sealed class ConfluenceOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "Confluence";

    /// <summary>
    /// The tick arm of the proximity band, in ticks: a level within <c>KTicks × tickSize</c> of the entry
    /// corroborates. Positive.
    /// </summary>
    public int KTicks { get; init; } = 8;

    /// <summary>
    /// The volatility arm of the proximity band, as an ATR multiple: a level within <c>FAtr × ATR</c> of the entry
    /// corroborates. The band is the <b>smaller</b> of the two arms (ADR-0026 §3), so it scales with the instrument's
    /// volatility rather than meaning something different on every contract. Positive.
    /// </summary>
    public decimal FAtr { get; init; } = 0.5m;

    /// <summary>
    /// The corroboration ladder: the timeframes (in minutes) whose same-signal reads and active levels are consulted.
    /// The fired trigger's own resolution is skipped (it is the primary, not a corroborator of itself). Every entry
    /// positive; empty leaves confluence inert (N=1). Default is a multi-timeframe intraday-to-daily ladder.
    /// </summary>
    public IReadOnlyList<int> TimeframeMinutes { get; init; } = [5, 15, 60, 240, 1440];

    /// <summary>
    /// Whether the knobs are usable — validated <b>on start</b> (Program.cs), because these feed the hosted scan and a
    /// non-positive <see cref="KTicks"/> or <see cref="FAtr"/> would mint a zero / negative proximity band that
    /// silently mis-measures every level, while a non-positive ladder entry would read a nonsensical timeframe. An
    /// empty ladder is deliberately allowed (it is the inert, opt-out configuration).
    /// </summary>
    /// <returns><see langword="true"/> when every knob is well-formed.</returns>
    public bool Validate() =>
        KTicks > 0 && FAtr > 0m && TimeframeMinutes is not null && TimeframeMinutes.All(timeframe => timeframe > 0);
}
