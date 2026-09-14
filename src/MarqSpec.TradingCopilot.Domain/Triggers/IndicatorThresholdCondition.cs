using MarqSpec.TradingCopilot.Domain;

namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// A trigger condition over a single pre-computed indicator value (R-22): "&lt;indicator&gt;(&lt;period&gt;) on
/// &lt;resolution&gt;m is &lt;below|above&gt; &lt;threshold&gt;" (R-4 / R-7, ADR-0008).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Instrument"/>, <see cref="Indicator"/>, <see cref="Period"/> and <see cref="ResolutionMinutes"/> are
/// exactly the <c>IIndicatorSource.GetValueAsync</c> read identity, so the evaluator's read is a straight forward.
/// <see cref="Indicator"/> is the R-22 stored name (<c>AtrIndicator.IndicatorName</c>, <c>RsiIndicator.IndicatorName</c>).
/// </para>
/// <para>
/// A <see langword="null"/> value means <b>cannot measure</b> and is answered <see cref="ConditionSatisfaction.Unmeasurable"/>
/// — never a default (a fallback would be the silent mis-measurement gh#311's refusal exists to prevent).
/// <see cref="Hysteresis"/> is an optional stateless dead-band: it widens the <i>re-arm</i> boundary only, so a
/// value hovering at the threshold reads <see cref="ConditionSatisfaction.Indeterminate"/> (hold) rather than
/// flickering fire / re-arm. Default <see langword="null"/> (0) makes the band inert.
/// </para>
/// </remarks>
public sealed record IndicatorThresholdCondition(
    InstrumentId Instrument,
    string Indicator,
    int Period,
    int ResolutionMinutes,
    IndicatorComparison Comparison,
    decimal Threshold,
    decimal? Hysteresis = null) : TriggerCondition
{
    /// <inheritdoc />
    public override ConditionSatisfaction Evaluate(decimal? indicatorValue)
    {
        if (indicatorValue is not { } value)
        {
            return ConditionSatisfaction.Unmeasurable; // null == cannot measure, never a default
        }

        decimal band = Hysteresis ?? 0m;
        return Comparison switch
        {
            IndicatorComparison.Below => value <= Threshold ? ConditionSatisfaction.Satisfied
                : value >= Threshold + band ? ConditionSatisfaction.NotSatisfied
                : ConditionSatisfaction.Indeterminate,
            IndicatorComparison.Above => value >= Threshold ? ConditionSatisfaction.Satisfied
                : value <= Threshold - band ? ConditionSatisfaction.NotSatisfied
                : ConditionSatisfaction.Indeterminate,
            _ => ConditionSatisfaction.Unmeasurable, // Unknown comparison: inert, never acts (fail-closed)
        };
    }
}
