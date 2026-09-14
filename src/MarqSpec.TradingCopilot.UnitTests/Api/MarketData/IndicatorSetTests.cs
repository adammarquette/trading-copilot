using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Domain.MarketData;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The composition of the bar-derived indicator set (R-22) — the single place the pipeline's indicators are
/// declared, pinned so the safety band's producer can never be dropped by a later edit.
/// </summary>
public class IndicatorSetTests
{
    [Fact]
    public void FromOptions_ShouldAlwaysIncludeAtr_AtTheSafetyBandPeriod()
    {
        // The stop-promotion band reads exactly (atr, AtrPeriod). Because the set is built in code rather than
        // from a free-form list, no configuration can silently omit it -- this pins that guarantee.
        IReadOnlyList<IIndicator> set = IndicatorSet.FromOptions(new IndicatorOptions { AtrPeriod = 21 });

        set.OfType<AtrIndicator>().Should().ContainSingle().Which.Period.Should().Be(21);
    }

    [Fact]
    public void FromOptions_ShouldIncludeRsi_AtItsConfiguredPeriod()
    {
        IReadOnlyList<IIndicator> set = IndicatorSet.FromOptions(new IndicatorOptions { RsiPeriod = 9 });

        set.OfType<RsiIndicator>().Should().ContainSingle().Which.Period.Should().Be(9);
    }

    [Fact]
    public void FromOptions_ShouldRejectNullOptions()
    {
        Action act = () => IndicatorSet.FromOptions(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
