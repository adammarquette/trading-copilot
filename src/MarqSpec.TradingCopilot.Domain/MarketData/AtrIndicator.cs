using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// The <see cref="IIndicator"/> adapter for average true range (R-22) — a thin, stateless forward to the pure
/// <see cref="AverageTrueRange"/> static, so the safety-critical ATR math is untouched by the framework around it.
/// </summary>
/// <param name="period">The ATR period this instance computes and stores under.</param>
public sealed class AtrIndicator(int period) : IIndicator
{
    /// <summary>The stored name for average true range. Canonical here; the projection aliases it.</summary>
    public const string IndicatorName = "atr";

    /// <inheritdoc />
    public string Name => IndicatorName;

    /// <inheritdoc />
    public int Period { get; } = period > 0
        ? period
        : throw new ArgumentOutOfRangeException(nameof(period), period, "The ATR period must be positive.");

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => AverageTrueRange.Compute(bars, Period);
}
