namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// The operator's configurable risk tolerance — what seeds position sizing and benchmarks the R:R KPI (R-5). It
/// sits <b>inside</b> the account's hard rules; where the two disagree, the tighter one wins.
/// </summary>
/// <param name="PerTradeRiskFraction">
/// Risk per trade as a fraction of <b>headroom to the drawdown floor</b> — deliberately not of account size, since
/// buying power is not the amount at risk. Sizing therefore tightens by itself as the floor is approached.
/// </param>
/// <param name="TargetRewardRatio">The target reward-to-risk ratio (e.g. 1.5 for 1.5 : 1).</param>
/// <param name="MaxDrawdownPerTrade">
/// The per-trade hard loss cap. This is where the safety stop is placed, which makes the worst case deterministic.
/// </param>
/// <param name="DailyDrawdownGovernor">
/// The personal daily-drawdown governor, set inside any hard daily limit so risk is managed before the wall.
/// </param>
/// <param name="DailyProfitTarget">The personal daily profit target, if set.</param>
/// <param name="StopForDayAtProfitTarget">
/// Whether reaching the daily target stops trading for the day — turning "hit $1,500 and stop" into an enforced
/// behavior rather than a reminder.
/// </param>
/// <param name="SizingBasis">Whether per-trade risk is sized to the working stop or the safety stop.</param>
public sealed record RiskProfile(
    decimal PerTradeRiskFraction,
    decimal TargetRewardRatio,
    decimal MaxDrawdownPerTrade,
    decimal DailyDrawdownGovernor,
    decimal? DailyProfitTarget,
    bool StopForDayAtProfitTarget,
    SizingBasis SizingBasis);
