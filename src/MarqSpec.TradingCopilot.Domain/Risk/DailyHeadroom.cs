namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// The remaining daily loss budget (R-5): how much more the account may lose today before its daily limits refuse
/// further risk. A pure projection of the declared limits over the day's realized loss — extracted from
/// <see cref="RiskGate"/> (gh#627) so the gate and any future daily-headroom projection (gh#587) share <b>one</b>
/// implementation rather than two that can drift. It decides nothing on its own; the gate consumes it. Absence of
/// a limit stays absent — it is never widened into a permissive default (R-5: absence is never a licence).
/// </summary>
/// <param name="UnderDailyLossLimit">
/// Room left under the firm's <b>hard</b> daily loss limit, or <see langword="null"/> when the firm imposes none
/// (Apex intraday-trail accounts). Null is honest absence: the caller decides this layer simply does not bind — as
/// the gate does — and it is never read as unlimited <i>room</i>.
/// </param>
/// <param name="UnderGovernor">Room left under the operator's daily drawdown governor, which is always declared.</param>
public readonly record struct DailyHeadroom(decimal? UnderDailyLossLimit, decimal UnderGovernor)
{
    /// <summary>
    /// The remaining daily budget, given the firm's optional hard daily loss limit, the operator's daily drawdown
    /// governor, and the day's realized loss as a <b>non-negative</b> amount —
    /// <see cref="AccountRiskState.DayLoss"/>, measured over the <b>CME trading day</b> (the venue's session), not
    /// midnight UTC. Pure subtraction in <see langword="decimal"/>: exact, so headroom is never rounded upward.
    /// </summary>
    /// <param name="dailyLossLimit">The firm's hard daily loss limit, or <see langword="null"/> where none applies.</param>
    /// <param name="dailyDrawdownGovernor">The operator's daily drawdown governor (always present).</param>
    /// <param name="dayLoss">The day's realized loss as a non-negative amount (zero on a green day).</param>
    /// <returns>The remaining budget under each daily limit.</returns>
    public static DailyHeadroom Remaining(decimal? dailyLossLimit, decimal dailyDrawdownGovernor, decimal dayLoss) =>
        new(dailyLossLimit is { } limit ? limit - dayLoss : null, dailyDrawdownGovernor - dayLoss);

    /// <summary>
    /// The firm's hard daily loss limit is fully spent — no room to add risk (the gate's
    /// <see cref="RiskLayer.DailyLossLimit"/> refusal). <see langword="false"/> when no limit is imposed: a missing
    /// limit does not bind this layer, exactly as the gate's <c>null &lt;= 0</c> comparison never fires.
    /// </summary>
    public bool DailyLossLimitSpent => UnderDailyLossLimit is { } remaining && remaining <= 0m;

    /// <summary>
    /// The operator's daily drawdown governor is fully spent (the gate's <see cref="RiskLayer.DailyGovernor"/>
    /// refusal).
    /// </summary>
    public bool GovernorSpent => UnderGovernor <= 0m;
}
