using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Domain.MarketData;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// The threshold bound is the indicator's OWN semantics, not "reject zero" (gh#1007). A value that makes the
/// inclusive comparison permanently satisfied — or unable to fire — is ADR-0019's silent monitor reached from
/// authoring, and must be refused before it can be stored.
/// </summary>
public class TriggerThresholdTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(70)]
    [InlineData(99)]
    public void Refusal_ShouldAcceptAnRsiThreshold_WhenStrictlyInsideZeroToHundred(decimal threshold)
    {
        TriggerThreshold.Refusal(RsiIndicator.IndicatorName, threshold).Should().BeNull();
    }

    [Theory]
    [InlineData(0)] // "rsi at or above 0" is permanently satisfied — the motivating case (gh#1007)
    [InlineData(-1)]
    [InlineData(100)] // "rsi at or below 100" is permanently satisfied
    [InlineData(150)]
    public void Refusal_ShouldRefuseAnRsiThresholdByName_WhenAtOrBeyondItsRange(decimal threshold)
    {
        TriggerThreshold.Refusal(RsiIndicator.IndicatorName, threshold).Should().Contain("RSI");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(1000)]
    public void Refusal_ShouldAcceptAnAtrThreshold_WhenPositive(decimal threshold)
    {
        TriggerThreshold.Refusal(AtrIndicator.IndicatorName, threshold).Should().BeNull();
    }

    [Theory]
    [InlineData(0)] // "atr at or above 0" is permanently satisfied — ATR is never negative
    [InlineData(-5)]
    public void Refusal_ShouldRefuseAnAtrThresholdByName_WhenNonPositive(decimal threshold)
    {
        TriggerThreshold.Refusal(AtrIndicator.IndicatorName, threshold).Should().Contain("ATR");
    }

    [Fact]
    public void Refusal_ShouldFailClosed_ForAnIndicatorWithNoRuleYet()
    {
        // The guard against the gh#1007 review's silent trap: an indicator added to the endpoints' known set but not
        // given a bound here must be REFUSED — in lockstep with the closed-world DB CHECK — never silently accept
        // every threshold and reopen the vulnerability. A missing rule is loud (no trigger authors), not silent.
        TriggerThreshold.Refusal("ema", 50m).Should().Contain("ema");
    }
}
