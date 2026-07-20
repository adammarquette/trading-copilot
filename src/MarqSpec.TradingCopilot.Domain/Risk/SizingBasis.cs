namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>Which stop the per-trade risk budget is divided by when sizing (configurable, R-5 / ADR-0007).</summary>
public enum SizingBasis
{
    /// <summary>Size against the working stop — the intended exit. Larger positions, ordinary risk realized.</summary>
    ActualStop,

    /// <summary>
    /// Size against the safety stop — the catastrophic exit. Smaller positions, because the budget must survive
    /// the worst case rather than the expected one.
    /// </summary>
    SafetyStop,
}
