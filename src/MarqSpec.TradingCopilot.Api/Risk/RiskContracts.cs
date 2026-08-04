using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Api.Risk;

/// <summary>
/// The request to declare an account's risk rules (R-5, gh#10) — the complete declaration; a redeclaration
/// replaces the whole profile.
/// </summary>
/// <param name="DailyLossLimit">The hard daily loss limit; null when the account has none.</param>
/// <param name="AccountProfitTarget">The account's profit target (evaluation accounts); null otherwise.</param>
/// <param name="StartingBalance">The account's declared starting balance — where the floor starts from. Positive.</param>
/// <param name="FloorSource">Whether the loss floor is firm-imposed or self-imposed.</param>
/// <param name="TrailingMode">How the drawdown floor trails — end-of-day or intraday.</param>
/// <param name="TrailingAmount">The trailing distance below the equity high-water mark. Positive.</param>
/// <param name="LocksAt">The ceiling the floor stops trailing at, if any.</param>
/// <param name="PerTradeRiskFraction">Risk per trade as a fraction of headroom to the floor — in (0, 1].</param>
/// <param name="TargetRewardRatio">The target reward-to-risk ratio. Positive.</param>
/// <param name="MaxDrawdownPerTrade">The per-trade hard loss cap. Positive.</param>
/// <param name="DailyDrawdownGovernor">The personal daily governor. Positive.</param>
/// <param name="DailyProfitTarget">The personal daily profit target; null means none.</param>
/// <param name="StopForDayAtProfitTarget">Whether reaching the daily target stops trading for the day.</param>
/// <param name="SizingBasis">Whether sizing uses the working stop or the safety stop.</param>
/// <param name="MaxContractsPerOrder">The manual per-order contract cap; 0 means uncapped.</param>
/// <param name="MaxBestDayFraction">The consistency target — best day ÷ total net profit, in (0, 1]; null means none.</param>
/// <param name="ConsistencyEnforcement">Whether breaching that target refuses the order or only warns (gh#380); only consulted when a target is set.</param>
/// <param name="DefaultEntryAction">Which entry action is primary on the Approve split button (R-11, gh#218). Defaults to <c>ApproveAndArm</c> (review-first); <c>SendAsIs</c> is practice-only and requires <paramref name="ConfirmSendAsIsDefault"/>.</param>
/// <param name="ConfirmSendAsIsDefault">The explicit confirmation required to default to <c>SendAsIs</c> — a request that sets <c>SendAsIs</c> without it is refused 422 (the kill switch's hold-to-confirm shape).</param>
public sealed record DeclareRiskProfileRequest(
    decimal? DailyLossLimit,
    decimal? AccountProfitTarget,
    decimal StartingBalance,
    FloorSource FloorSource,
    TrailingMode TrailingMode,
    decimal TrailingAmount,
    decimal? LocksAt,
    decimal PerTradeRiskFraction,
    decimal TargetRewardRatio,
    decimal MaxDrawdownPerTrade,
    decimal DailyDrawdownGovernor,
    decimal? DailyProfitTarget,
    bool StopForDayAtProfitTarget,
    SizingBasis SizingBasis,
    int MaxContractsPerOrder,
    decimal? MaxBestDayFraction,
    ConsistencyEnforcement ConsistencyEnforcement = ConsistencyEnforcement.Advisory,
    DefaultEntryAction DefaultEntryAction = DefaultEntryAction.ApproveAndArm,
    bool ConfirmSendAsIsDefault = false);

/// <summary>An account's declared risk rules, as persisted.</summary>
/// <param name="AccountId">The account the rules govern.</param>
/// <param name="DailyLossLimit">The hard daily loss limit; null when none.</param>
/// <param name="AccountProfitTarget">The account's profit target; null when none.</param>
/// <param name="StartingBalance">The declared starting balance the floor starts from.</param>
/// <param name="FloorSource">Whether the loss floor is firm-imposed or self-imposed.</param>
/// <param name="TrailingMode">How the drawdown floor trails.</param>
/// <param name="TrailingAmount">The trailing distance.</param>
/// <param name="LocksAt">The floor's lock ceiling, if any.</param>
/// <param name="PerTradeRiskFraction">Risk per trade as a fraction of headroom.</param>
/// <param name="TargetRewardRatio">The target reward-to-risk ratio.</param>
/// <param name="MaxDrawdownPerTrade">The per-trade hard loss cap.</param>
/// <param name="DailyDrawdownGovernor">The personal daily governor.</param>
/// <param name="DailyProfitTarget">The daily profit target; null when none.</param>
/// <param name="StopForDayAtProfitTarget">Whether reaching the target stops the day.</param>
/// <param name="SizingBasis">The sizing basis.</param>
/// <param name="MaxContractsPerOrder">The manual per-order cap; 0 means uncapped.</param>
/// <param name="MaxBestDayFraction">The consistency target; null when none.</param>
/// <param name="ConsistencyEnforcement">Whether breaching that target refuses the order or only warns (gh#380).</param>
/// <param name="DefaultEntryAction">Which entry action is primary on the Approve split button (R-11, gh#218).</param>
public sealed record RiskProfileResponse(
    Guid AccountId,
    decimal? DailyLossLimit,
    decimal? AccountProfitTarget,
    decimal StartingBalance,
    FloorSource FloorSource,
    TrailingMode TrailingMode,
    decimal TrailingAmount,
    decimal? LocksAt,
    decimal PerTradeRiskFraction,
    decimal TargetRewardRatio,
    decimal MaxDrawdownPerTrade,
    decimal DailyDrawdownGovernor,
    decimal? DailyProfitTarget,
    bool StopForDayAtProfitTarget,
    SizingBasis SizingBasis,
    int MaxContractsPerOrder,
    decimal? MaxBestDayFraction,
    ConsistencyEnforcement ConsistencyEnforcement,
    DefaultEntryAction DefaultEntryAction)
{
    /// <summary>Projects a persisted declaration to the response shape.</summary>
    /// <param name="record">The persisted declaration.</param>
    /// <returns>The response.</returns>
    public static RiskProfileResponse From(RiskProfileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new RiskProfileResponse(
            record.AccountId,
            record.DailyLossLimit,
            record.AccountProfitTarget,
            record.StartingBalance,
            record.FloorSource,
            record.TrailingMode,
            record.TrailingAmount,
            record.LocksAt,
            record.PerTradeRiskFraction,
            record.TargetRewardRatio,
            record.MaxDrawdownPerTrade,
            record.DailyDrawdownGovernor,
            record.DailyProfitTarget,
            record.StopForDayAtProfitTarget,
            record.SizingBasis,
            record.MaxContractsPerOrder,
            record.MaxBestDayFraction,
            record.ConsistencyEnforcement,
            record.DefaultEntryAction);
    }
}

/// <summary>
/// Today's remaining daily loss budget and target progress (gh#587, R-5), readable <b>without composing a send</b> —
/// the proactive, suggestion-time governor's read surface (R-4 consumer, gh#551) over the <b>same</b>
/// <see cref="DailyHeadroom"/> arithmetic the send-time gate enforces (extracted gh#627), so a read can never be a
/// second definition of the limit.
/// </summary>
/// <param name="UnderGovernor">Room left under the operator's daily drawdown governor (always present).</param>
/// <param name="UnderDailyLossLimit">Room left under the firm's hard daily loss limit, or <see langword="null"/> when none is imposed.</param>
/// <param name="GovernorSpent">The governor is fully spent — the gate's <c>DailyGovernor</c> refusal.</param>
/// <param name="DailyLossLimitSpent">The hard daily loss limit is fully spent — the gate's <c>DailyLossLimit</c> refusal.</param>
/// <param name="DayLoss">Today's realized loss as a non-negative amount (zero on a green day) — the input consumed.</param>
/// <param name="DayRealizedProfit">Today's realized profit as a non-negative amount (zero on a red day) — for target progress.</param>
/// <param name="DailyProfitTarget">The daily profit target, or <see langword="null"/> when none is declared.</param>
/// <param name="DailyTargetReached">
/// The profit target is reached <b>and</b> stand-down is enabled — the gate's stop-for-day condition (a breach the
/// operator should heed, not a hard refusal to add risk).
/// </param>
public sealed record DailyHeadroomResponse(
    decimal UnderGovernor,
    decimal? UnderDailyLossLimit,
    bool GovernorSpent,
    bool DailyLossLimitSpent,
    decimal DayLoss,
    decimal DayRealizedProfit,
    decimal? DailyProfitTarget,
    bool DailyTargetReached)
{
    /// <summary>Projects the computed headroom and the day's realized figures into the response shape.</summary>
    /// <param name="headroom">The remaining budget under each daily limit.</param>
    /// <param name="dayLoss">Today's realized loss (non-negative).</param>
    /// <param name="dayRealizedProfit">Today's realized profit (non-negative).</param>
    /// <param name="profile">The account's declared risk profile — the target and stand-down flag.</param>
    /// <returns>The response.</returns>
    public static DailyHeadroomResponse From(
        DailyHeadroom headroom, decimal dayLoss, decimal dayRealizedProfit, RiskProfileRecord profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new DailyHeadroomResponse(
            headroom.UnderGovernor,
            headroom.UnderDailyLossLimit,
            headroom.GovernorSpent,
            headroom.DailyLossLimitSpent,
            dayLoss,
            dayRealizedProfit,
            profile.DailyProfitTarget,
            // The gate's stop-for-day condition (RiskGate): stand-down enabled AND the target is reached on realized.
            profile.StopForDayAtProfitTarget && profile.DailyProfitTarget is { } target && dayRealizedProfit >= target);
    }
}
