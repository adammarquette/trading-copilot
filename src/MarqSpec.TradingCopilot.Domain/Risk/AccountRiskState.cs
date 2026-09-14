namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// Live account state as the venue reports it, which is what the risk layers are measured against (R-5). Sourced
/// from the venue's account slice, never from a local guess.
/// </summary>
/// <param name="Balance">The realized cash balance, already including today's closed P&amp;L.</param>
/// <param name="UnrealizedPnL">Open-position P&amp;L; negative when underwater.</param>
/// <param name="DayRealizedPnL">Today's closed P&amp;L; negative on a losing day.</param>
public sealed record AccountRiskState(
    decimal Balance,
    decimal UnrealizedPnL,
    decimal DayRealizedPnL)
{
    /// <summary>Balance plus open P&amp;L — what the drawdown floor is actually measured against.</summary>
    public decimal Equity => Balance + UnrealizedPnL;

    /// <summary>
    /// The day's loss as a positive number, zero on a green day. Counts open positions: the daily limits are
    /// <i>live</i> headroom, so an underwater trade has already spent part of the day's budget.
    /// </summary>
    public decimal DayLoss => Math.Max(0m, -(DayRealizedPnL + UnrealizedPnL));
}
