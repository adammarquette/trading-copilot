using MarqSpec.TradingCopilot.Domain.MarketData;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Builds the bar-derived indicators the projection computes (R-22), from <see cref="IndicatorOptions"/> — the
/// single place the pipeline's indicator set is declared, so the composition root and its test agree by
/// construction.
/// </summary>
/// <remarks>
/// <b>ATR is always present at <see cref="IndicatorOptions.AtrPeriod"/></b>: the stop-promotion band reads that
/// exact <c>(atr, AtrPeriod)</c> series, so building the set in code — rather than from a free-form list an
/// operator could edit — means the safety band's producer cannot be configured away. Adding a third indicator is
/// one more entry here.
/// </remarks>
public static class IndicatorSet
{
    /// <summary>The indicators to project, in a fixed order.</summary>
    /// <param name="options">The configured periods.</param>
    /// <returns>The indicator set — ATR (the safety band's producer) and RSI.</returns>
    public static IReadOnlyList<IIndicator> FromOptions(IndicatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return
        [
            new AtrIndicator(options.AtrPeriod),
            new RsiIndicator(options.RsiPeriod),
        ];
    }
}
