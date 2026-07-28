using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// Bridges the persisted risk declaration to the domain vocabulary the gate consumes (gh#10) — the
/// <c>Firm.ToConventions()</c> pattern: one bridge, so the gate can never see numbers the entity does not hold.
/// </summary>
public static class RiskProfileMapping
{
    /// <summary>The operator's tolerance, revalidated through <see cref="RiskProfile.Declare"/>.</summary>
    /// <param name="record">The persisted declaration.</param>
    /// <returns>The domain profile.</returns>
    public static RiskProfile ToRiskProfile(this RiskProfileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return RiskProfile.Declare(
            record.PerTradeRiskFraction,
            record.TargetRewardRatio,
            record.MaxDrawdownPerTrade,
            record.DailyDrawdownGovernor,
            record.DailyProfitTarget,
            record.StopForDayAtProfitTarget,
            record.SizingBasis);
    }

    /// <summary>The account's hard rules.</summary>
    /// <param name="record">The persisted declaration.</param>
    /// <returns>The domain rules.</returns>
    public static AccountRiskRules ToAccountRiskRules(this RiskProfileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // MaxBestDayFraction IS the consistency target (gh#380). It has been persisted, validated and exposed on
        // the risk API since the profile landed, but was never mapped through to the domain -- so the gate could
        // not see it and the rule was configurable without being enforced. The names differ deliberately: the
        // column says what it measures (best day / total), the domain rule says what it is.
        return new AccountRiskRules(
            record.DailyLossLimit,
            record.AccountProfitTarget,
            record.FloorSource,
            record.MaxBestDayFraction,
            record.ConsistencyEnforcement);
    }

    /// <summary>
    /// Starts the trailing floor from the persisted <i>configuration</i>. The floor itself is runtime state the
    /// risk engine advances, so the caller supplies the balance it starts from.
    /// </summary>
    /// <param name="record">The persisted declaration.</param>
    /// <param name="startingBalance">The balance the floor starts trailing from.</param>
    /// <returns>The opening drawdown state.</returns>
    public static TrailingDrawdown ToTrailingDrawdown(this RiskProfileRecord record, decimal startingBalance)
    {
        ArgumentNullException.ThrowIfNull(record);
        return TrailingDrawdown.Start(record.TrailingMode, record.TrailingAmount, startingBalance, record.LocksAt);
    }

    /// <summary>The manual caps. Per-instrument caps arrive with the Instrument entity.</summary>
    /// <param name="record">The persisted declaration.</param>
    /// <returns>The domain caps.</returns>
    public static ManualCaps ToManualCaps(this RiskProfileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ManualCaps.Create(record.MaxContractsPerOrder);
    }
}
