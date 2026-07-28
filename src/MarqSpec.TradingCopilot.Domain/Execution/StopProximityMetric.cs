namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// How a <see cref="StopProximity"/> band is measured (ADR-0007). Never a percentage of raw price.
/// </summary>
public enum StopProximityMetric
{
    /// <summary>Not a metric — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>Whole ticks of the instrument.</summary>
    Ticks = 1,

    /// <summary>A fraction of the entry→actual-stop distance — scales with the risk taken, not the price level.</summary>
    DistanceFraction = 2,

    /// <summary>Multiples of average true range — measured by the indicator pipeline (R-22, gh#310) and resolved to a distance by the caller (StopProximity.ResolveDistance, gh#311).</summary>
    AverageTrueRange = 3,
}
