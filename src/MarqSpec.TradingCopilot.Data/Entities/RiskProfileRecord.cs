using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// The operator's declared risk rules for one account (data dictionary §5 "RiskProfile / Limits", R-5) — the
/// persistence <b>spine</b> (gh#10): the hard rules, the trailing-floor <i>configuration</i>, the tolerance,
/// the manual cap, and the consistency-target fraction. One row per account.
/// </summary>
/// <remarks>
/// <para>
/// Named <c>RiskProfileRecord</c> because the domain vocabulary it persists already owns the name
/// <c>RiskProfile</c>; the table is still <c>RiskProfiles</c>. Bridged by <c>RiskProfileMapping</c> — the
/// <c>Firm.ToConventions()</c> pattern — so the gate composes from exactly what is persisted.
/// </para>
/// <para>
/// Deferred deliberately: the <b>current floor</b> (runtime state the risk engine advances — only the trailing
/// <i>config</i> lives here), per-instrument caps (need the Instrument entity), kill-switch mode and the
/// per-instrument auto-flatten config (no domain vocabulary yet), and <c>GateDecision</c> persistence (lands
/// with the S3 gate runtime). The three declaration enums (<see cref="FloorSource"/>,
/// <see cref="TrailingMode"/>, <see cref="SizingBasis"/>) predate the fail-closed-zero convention, so their
/// zeros are real values: <c>required</c> forces explicit construction, and the PUT boundary validates through
/// the domain factories.
/// </para>
/// </remarks>
public class RiskProfileRecord : IUserOwned
{
    /// <summary>The record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The account these rules govern — one profile per account.</summary>
    public Guid AccountId { get; set; }

    /// <summary>
    /// The account's declared starting balance — where the trailing floor starts from (gh#11). Positive,
    /// DB-checked. The high-water mark the floor truly trails is runtime state (deferred); until it lands, the
    /// floor starts from max(this, live balance), bounded by <see cref="LocksAt"/>.
    /// </summary>
    public required decimal StartingBalance { get; set; }

    /// <summary>The hard daily loss limit; null means the account has none (absent, not zero).</summary>
    public decimal? DailyLossLimit { get; set; }

    /// <summary>The account's profit target, where one applies (evaluation accounts).</summary>
    public decimal? AccountProfitTarget { get; set; }

    /// <summary>Whether the loss floor is firm-imposed (account-fail) or self-imposed (halt + flatten).</summary>
    public required FloorSource FloorSource { get; set; }

    /// <summary>How the drawdown floor trails — end-of-day or intraday.</summary>
    public required TrailingMode TrailingMode { get; set; }

    /// <summary>The trailing distance below the equity high-water mark. Positive — a DB check refuses the rest.</summary>
    public required decimal TrailingAmount { get; set; }

    /// <summary>The ceiling the floor stops trailing at, if any (e.g. breakeven lock).</summary>
    public decimal? LocksAt { get; set; }

    /// <summary>Risk per trade as a fraction of headroom to the floor — in (0, 1], DB-checked.</summary>
    public required decimal PerTradeRiskFraction { get; set; }

    /// <summary>The target reward-to-risk ratio (e.g. 1.5). Positive, DB-checked.</summary>
    public required decimal TargetRewardRatio { get; set; }

    /// <summary>The per-trade hard loss cap — where the safety stop goes. Positive, DB-checked.</summary>
    public required decimal MaxDrawdownPerTrade { get; set; }

    /// <summary>The personal daily-drawdown governor, inside any hard daily limit. Positive, DB-checked.</summary>
    public required decimal DailyDrawdownGovernor { get; set; }

    /// <summary>The personal daily profit target; null means none.</summary>
    public decimal? DailyProfitTarget { get; set; }

    /// <summary>Whether reaching the daily target stops trading for the day.</summary>
    public bool StopForDayAtProfitTarget { get; set; }

    /// <summary>Whether per-trade risk is sized to the working stop or the safety stop.</summary>
    public required SizingBasis SizingBasis { get; set; }

    /// <summary>The manual per-order contract cap; 0 means uncapped (mirrors <c>ManualCaps</c>).</summary>
    public required int MaxContractsPerOrder { get; set; }

    /// <summary>
    /// The consistency target (gh#10): the largest single day as a fraction of total net profit — in (0, 1]
    /// when set, DB-checked. Config only; the evaluation over P&amp;L-by-day arrives with the risk runtime.
    /// </summary>
    public decimal? MaxBestDayFraction { get; set; }
}
