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
    SizingBasis SizingBasis)
{
    /// <summary>
    /// Declares a profile with the invariants enforced (the <c>FirmConventions.For</c> pattern): boundaries
    /// construct through here, so a profile that would size nothing — or everything — never reaches the gate.
    /// </summary>
    /// <param name="perTradeRiskFraction">Risk per trade as a fraction of headroom — in (0, 1].</param>
    /// <param name="targetRewardRatio">The target reward-to-risk ratio — positive.</param>
    /// <param name="maxDrawdownPerTrade">The per-trade hard loss cap — positive.</param>
    /// <param name="dailyDrawdownGovernor">The personal daily governor — positive.</param>
    /// <param name="dailyProfitTarget">The daily profit target — positive when set; null means none.</param>
    /// <param name="stopForDayAtProfitTarget">Whether reaching the target stops the day.</param>
    /// <param name="sizingBasis">Whether sizing uses the working stop or the safety stop.</param>
    /// <returns>The validated profile.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its declared range.</exception>
    public static RiskProfile Declare(
        decimal perTradeRiskFraction,
        decimal targetRewardRatio,
        decimal maxDrawdownPerTrade,
        decimal dailyDrawdownGovernor,
        decimal? dailyProfitTarget,
        bool stopForDayAtProfitTarget,
        SizingBasis sizingBasis)
    {
        if (perTradeRiskFraction <= 0m || perTradeRiskFraction > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(perTradeRiskFraction), perTradeRiskFraction, "The per-trade risk fraction must be in (0, 1].");
        }

        if (targetRewardRatio <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetRewardRatio), targetRewardRatio, "The target reward ratio must be positive.");
        }

        if (maxDrawdownPerTrade <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDrawdownPerTrade), maxDrawdownPerTrade, "The per-trade drawdown cap must be positive.");
        }

        if (dailyDrawdownGovernor <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dailyDrawdownGovernor), dailyDrawdownGovernor, "The daily governor must be positive.");
        }

        if (dailyProfitTarget is <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dailyProfitTarget), dailyProfitTarget, "A daily profit target must be positive when set — null means none.");
        }

        return new RiskProfile(
            perTradeRiskFraction,
            targetRewardRatio,
            maxDrawdownPerTrade,
            dailyDrawdownGovernor,
            dailyProfitTarget,
            stopForDayAtProfitTarget,
            sizingBasis);
    }
}
