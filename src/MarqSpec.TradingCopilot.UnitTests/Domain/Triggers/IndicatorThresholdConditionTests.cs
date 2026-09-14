using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Triggers;

/// <summary>
/// The indicator-threshold trigger condition (R-4 / R-7, ADR-0008) — a pure predicate over one R-22 read. A null
/// value must read <see cref="ConditionSatisfaction.Unmeasurable"/> (fail-closed, never a default), and the
/// hysteresis dead-band must be held apart from both a data gap and a clear reading.
/// </summary>
public class IndicatorThresholdConditionTests
{
    private static IndicatorThresholdCondition Below(decimal threshold, decimal? hysteresis = null) =>
        new(InstrumentId.Parse("ES"), "rsi", 14, 1, IndicatorComparison.Below, threshold, hysteresis);

    private static IndicatorThresholdCondition Above(decimal threshold, decimal? hysteresis = null) =>
        new(InstrumentId.Parse("ES"), "rsi", 14, 1, IndicatorComparison.Above, threshold, hysteresis);

    // --- Fail-closed on a null indicator (the safety property) ---

    [Fact]
    public void Evaluate_ShouldBeUnmeasurable_WhenTheValueIsNull()
    {
        // null == cannot measure. Answering NotSatisfied would let a data gap re-arm a fired trigger.
        Below(30m).Evaluate(null).Should().Be(ConditionSatisfaction.Unmeasurable);
    }

    [Fact]
    public void Evaluate_ShouldBeUnmeasurable_WhenTheComparisonIsUnknown()
    {
        IndicatorThresholdCondition corrupt = new(
            InstrumentId.Parse("ES"), "rsi", 14, 1, IndicatorComparison.Unknown, 30m);

        corrupt.Evaluate(25m).Should().Be(ConditionSatisfaction.Unmeasurable);
    }

    // --- Below ---

    [Theory]
    [InlineData(30, ConditionSatisfaction.Satisfied)] // at the threshold satisfies
    [InlineData(25, ConditionSatisfaction.Satisfied)] // clearly below
    [InlineData(31, ConditionSatisfaction.NotSatisfied)] // above, no hysteresis
    public void Evaluate_Below_WithoutHysteresis(decimal value, ConditionSatisfaction expected)
    {
        Below(30m).Evaluate(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(30, ConditionSatisfaction.Satisfied)] // at/below the fire boundary
    [InlineData(33, ConditionSatisfaction.NotSatisfied)] // at/above threshold + band (32) -> clear
    [InlineData(31, ConditionSatisfaction.Indeterminate)] // above threshold but inside the 2-wide re-arm band
    public void Evaluate_Below_WithHysteresis(decimal value, ConditionSatisfaction expected)
    {
        Below(30m, hysteresis: 2m).Evaluate(value).Should().Be(expected);
    }

    // --- Above (the mirror) ---

    [Theory]
    [InlineData(70, ConditionSatisfaction.Satisfied)]
    [InlineData(75, ConditionSatisfaction.Satisfied)]
    [InlineData(69, ConditionSatisfaction.NotSatisfied)]
    public void Evaluate_Above_WithoutHysteresis(decimal value, ConditionSatisfaction expected)
    {
        Above(70m).Evaluate(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(70, ConditionSatisfaction.Satisfied)]
    [InlineData(67, ConditionSatisfaction.NotSatisfied)] // at/below threshold - band (68) -> clear
    [InlineData(69, ConditionSatisfaction.Indeterminate)] // below threshold but inside the band
    public void Evaluate_Above_WithHysteresis(decimal value, ConditionSatisfaction expected)
    {
        Above(70m, hysteresis: 2m).Evaluate(value).Should().Be(expected);
    }

    [Fact]
    public void Evaluate_ShouldNeverBeIndeterminate_WhenHysteresisIsInert()
    {
        // With no band, every measured value is cleanly Satisfied or NotSatisfied -- there is no dead-band.
        IndicatorThresholdCondition condition = Below(30m); // hysteresis null

        condition.Evaluate(30.0001m).Should().Be(ConditionSatisfaction.NotSatisfied);
        condition.Evaluate(30m).Should().Be(ConditionSatisfaction.Satisfied);
    }
}
