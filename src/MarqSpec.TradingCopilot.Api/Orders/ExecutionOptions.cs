using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Api.Orders;

/// <summary>
/// The execution sanity caps (R-16), bound from the <c>Execution</c> config section. These are the last-line
/// fat-finger bounds — deliberately conservative defaults, overridable per environment, and <b>independent</b>
/// of the operator's declared per-account risk rules (which live on the account's RiskProfile).
/// </summary>
public sealed class ExecutionOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Execution";

    /// <summary>The absolute per-order contract ceiling, whatever any profile says.</summary>
    public int MaxContractsPerOrder { get; init; } = 5;

    /// <summary>The absolute per-order notional ceiling.</summary>
    public decimal MaxNotional { get; init; } = 250_000m;

    /// <summary>How far from the reference price an order may rest, in ticks.</summary>
    public int FatFingerBandTicks { get; init; } = 200;

    /// <summary>
    /// How close price must come to the working stop before it is promoted from hidden to a native working
    /// order (ADR-0007, gh#11), in ticks. The band is deliberately tick-based, never a percentage of raw price.
    /// </summary>
    public int StopPromotionTicks { get; init; } = 8;

    /// <summary>The domain caps these options configure.</summary>
    /// <returns>The sanity caps.</returns>
    public SanityCaps ToSanityCaps() => new(MaxContractsPerOrder, MaxNotional, FatFingerBandTicks);
}
