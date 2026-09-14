using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// The <see cref="IIndicator"/> adapter for the relative strength index (R-22) — a thin, stateless forward to the
/// pure <see cref="RelativeStrengthIndex"/> static.
/// </summary>
/// <param name="period">The RSI period this instance computes and stores under.</param>
public sealed class RsiIndicator(int period) : IIndicator
{
    /// <summary>The stored name for the relative strength index.</summary>
    public const string IndicatorName = "rsi";

    /// <inheritdoc />
    public string Name => IndicatorName;

    /// <inheritdoc />
    public int Period { get; } = period > 0
        ? period
        : throw new ArgumentOutOfRangeException(nameof(period), period, "The RSI period must be positive.");

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => RelativeStrengthIndex.Compute(bars, Period);
}
