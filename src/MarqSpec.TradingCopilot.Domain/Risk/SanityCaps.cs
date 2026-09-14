namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// Execution sanity caps (R-16) — the last line of defense before transmission, independent of the risk model.
/// These exist to stop the obviously wrong ticket: a fat-fingered size, an extra zero, a price from another
/// session. They apply on every path, manual or suggested, in practice and in live alike.
/// </summary>
/// <param name="MaxContractsPerOrder">A hard ceiling on contracts in one order, whatever the risk layers allow.</param>
/// <param name="MaxNotional">A hard ceiling on the order's notional value.</param>
/// <param name="FatFingerBandTicks">
/// How far, in ticks, an entry may sit from the current market before it is refused as a mistake.
/// </param>
public sealed record SanityCaps(
    int MaxContractsPerOrder,
    decimal MaxNotional,
    int FatFingerBandTicks);
