using MarqSpec.TradingCopilot.Domain.MarketData;

namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// Refuses an indicator threshold that would make a standing trigger <b>permanently satisfied</b> — or unable to
/// ever fire — which the debounce seeds straight to <c>Fired</c> and holds there: ADR-0019's silent monitor, reached
/// from the authoring end rather than by drift (gh#1007).
/// </summary>
/// <remarks>
/// The bound is the <b>indicator's own semantics</b>, not a guess like "reject zero" — because <c>0</c> is a
/// legitimate value for some indicators and not others. The comparison is inclusive (<i>at or above</i> / <i>at or
/// below</i>), so:
/// <list type="bullet">
/// <item><b>RSI</b> is definitionally bounded to <c>0–100</c>; a threshold at or beyond a bound makes one direction
/// always-true (e.g. <c>rsi ≥ 0</c>) and the other never-true. A meaningful threshold is <b>strictly inside</b>:
/// <c>0 &lt; t &lt; 100</c>.</item>
/// <item><b>ATR</b> is a non-negative magnitude (an average of true ranges); a non-positive threshold makes
/// <c>atr ≥ t</c> always-true. It is unbounded above, so the rule is simply <c>t &gt; 0</c>.</item>
/// </list>
/// This is the endpoint-side refusal; <c>CK_Triggers_Threshold_InIndicatorRange</c> backstops it below any writer.
/// </remarks>
internal static class TriggerThreshold
{
    /// <summary>A by-name refusal for an out-of-range threshold, or <c>null</c> when it is valid for the indicator.</summary>
    /// <param name="indicator">The canonical indicator name (the endpoints resolve/validate it first).</param>
    /// <param name="threshold">The proposed threshold.</param>
    internal static string? Refusal(string indicator, decimal threshold) =>
        indicator switch
        {
            RsiIndicator.IndicatorName => threshold is > 0m and < 100m
                ? null
                : "An RSI threshold must be strictly inside its 0–100 range — a value at or beyond a bound makes the "
                  + "trigger permanently satisfied or unable to fire (ADR-0019).",
            AtrIndicator.IndicatorName => threshold > 0m
                ? null
                : "An ATR threshold must be positive — ATR is never negative, so a non-positive one makes the trigger "
                  + "permanently satisfied (ADR-0019).",
            // An unknown indicator is refused before this reaches here (the endpoints' known-indicator gate), so
            // there is no threshold rule to add for it — never second-guess that refusal from here.
            _ => null,
        };
}
