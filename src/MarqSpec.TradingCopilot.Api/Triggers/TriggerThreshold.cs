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
/// The exact-extreme thresholds (<c>rsi</c> 0 / 100, <c>atr</c> 0) are therefore <b>deliberately un-authorable</b>: a
/// trigger that can fire only at an indicator's absolute extreme is indistinguishable from one that never fires — the
/// same silent-monitor risk. This is the endpoint-side refusal; <c>CK_Triggers_Threshold_InIndicatorRange</c>
/// backstops it below any writer, and the two are kept in lockstep (see the default arm below).
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
            // Fail CLOSED, in lockstep with the closed-world DB CHECK (CK_Triggers_Threshold_InIndicatorRange refuses
            // any Indicator that is not rsi/atr). An indicator reaches here ONLY once it is in the endpoints'
            // known-indicator gate, so this fires only for a NEW indicator whose bound nobody added yet — refusing it
            // makes that omission loud (no trigger authors for it) rather than silently reopening gh#1007 by accepting
            // every threshold. Add the indicator's rule above AND its arm to the CHECK, together.
            _ => $"No threshold rule is defined for the '{indicator}' indicator — add its bound to TriggerThreshold "
                 + "and CK_Triggers_Threshold_InIndicatorRange before authoring triggers for it (gh#1007).",
        };
}
