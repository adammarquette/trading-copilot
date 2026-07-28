using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Triggers;

/// <summary>
/// The indicator-threshold condition (gh#385). The quiet failure it guards is firing on a value we never
/// measured: a null indicator must be <see cref="ConditionEvaluation.Unmeasurable"/>, never coerced to a default
/// (fail-closed, gh#311).
/// </summary>
public class IndicatorThresholdConditionTests
{
    private static IndicatorThresholdCondition Below30() =>
        new(InstrumentId.Parse("ES"), "rsi", 14, 1, ThresholdComparison.Below, 30m);

    private static IndicatorThresholdCondition Above70() =>
        new(InstrumentId.Parse("ES"), "rsi", 14, 1, ThresholdComparison.Above, 70m);

    [Fact]
    public void Evaluate_ShouldBeUnmeasurable_WhenTheValueIsNull()
    {
        // THE fail-closed guard: no value means we cannot tell which side of the threshold we are on.
        Below30().Evaluate(null).Should().Be(ConditionEvaluation.Unmeasurable);
    }

    [Theory]
    [InlineData(29, ConditionEvaluation.Satisfied)]
    [InlineData(30, ConditionEvaluation.Satisfied)] // inclusive boundary
    [InlineData(31, ConditionEvaluation.NotSatisfied)]
    public void Evaluate_Below_ShouldSatisfyAtOrBelowTheThreshold(decimal value, ConditionEvaluation expected)
    {
        Below30().Evaluate(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(71, ConditionEvaluation.Satisfied)]
    [InlineData(70, ConditionEvaluation.Satisfied)] // inclusive boundary
    [InlineData(69, ConditionEvaluation.NotSatisfied)]
    public void Evaluate_Above_ShouldSatisfyAtOrAboveTheThreshold(decimal value, ConditionEvaluation expected)
    {
        Above70().Evaluate(value).Should().Be(expected);
    }

    [Fact]
    public void Constructor_ShouldRefuseTheUnknownComparison()
    {
        // The refusable zero: an unset comparison must never construct, or a trigger could satisfy on a default.
        Action act = () => _ = new IndicatorThresholdCondition(
            InstrumentId.Parse("ES"), "rsi", 14, 1, ThresholdComparison.Unknown, 30m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ShouldRefuseAnOutOfRangeComparison()
    {
        // A raw client can bind an integer past the enum; the allowlist (Below/Above) must refuse it.
        Action act = () => _ = new IndicatorThresholdCondition(
            InstrumentId.Parse("ES"), "rsi", 14, 1, (ThresholdComparison)99, 30m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ShouldRefuseABlankIndicator()
    {
        Action act = () => _ = new IndicatorThresholdCondition(
            InstrumentId.Parse("ES"), "   ", 14, 1, ThresholdComparison.Below, 30m);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldRefuseANonPositivePeriod(int period)
    {
        Action act = () => _ = new IndicatorThresholdCondition(
            InstrumentId.Parse("ES"), "rsi", period, 1, ThresholdComparison.Below, 30m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_ShouldRefuseANonPositiveResolution(int resolution)
    {
        Action act = () => _ = new IndicatorThresholdCondition(
            InstrumentId.Parse("ES"), "rsi", 14, resolution, ThresholdComparison.Below, 30m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ShouldNormalizeTheIndicatorNameToLowerCase()
    {
        // The stored name must match the projection's own identity ("rsi"), whatever case the operator typed.
        new IndicatorThresholdCondition(InstrumentId.Parse("ES"), "RSI", 14, 1, ThresholdComparison.Below, 30m)
            .Indicator.Should().Be("rsi");
    }
}
