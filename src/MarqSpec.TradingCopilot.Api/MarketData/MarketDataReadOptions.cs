namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Bounds on the read-only market-data surface (gh#644, R-10). A windowed read (bars, indicator series) is capped
/// at <see cref="MaxRows"/> rows; a window that would return more is <b>refused with a clear error, never silently
/// truncated</b> — an unbounded <c>from</c> against a multi-year bar store is a trivial way to pull the API over,
/// and a truncated series drawn as if complete would mislead the chart.
/// </summary>
public sealed class MarketDataReadOptions
{
    /// <summary>The configuration section that binds this options object.</summary>
    public const string SectionName = "MarketDataRead";

    /// <summary>
    /// The most rows a single windowed bars / indicator read returns. A window whose result would exceed this is
    /// refused (400) so the caller narrows the window or asks for a coarser resolution. Default 5000 — comfortably
    /// more than a chart viewport, far short of a denial-of-service.
    /// </summary>
    public int MaxRows { get; set; } = 5000;
}
