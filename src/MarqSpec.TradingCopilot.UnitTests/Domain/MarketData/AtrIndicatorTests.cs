using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.MarketData;

/// <summary>
/// The <see cref="AtrIndicator"/> adapter (R-22) — proves it is a faithful, thin forward to the untouched
/// <see cref="AverageTrueRange"/> math, so the framework wraps the safety-critical calculation without altering it.
/// </summary>
public class AtrIndicatorTests
{
    private static DateTimeOffset Open(int minute) => new(2026, 7, 20, 14, minute, 0, TimeSpan.Zero);

    private static Bar Bar(int minute, decimal high, decimal low, decimal close) =>
        new(Open(minute), new Price(low), new Price(high), new Price(low), new Price(close), 100);

    private static IReadOnlyList<Bar> Series() =>
    [
        Bar(0, 100, 100, 100),
        Bar(1, 110, 100, 105),
        Bar(2, 125, 105, 120),
        Bar(3, 150, 120, 140),
        Bar(4, 190, 140, 180),
    ];

    [Fact]
    public void Name_ShouldBeTheStoredAtrKey()
    {
        new AtrIndicator(14).Name.Should().Be("atr");
    }

    [Fact]
    public void Period_ShouldEchoTheConstructorArgument()
    {
        new AtrIndicator(14).Period.Should().Be(14);
    }

    [Fact]
    public void Compute_ShouldForwardVerbatimToTheAverageTrueRangeStatic()
    {
        // The load-bearing pin: the adapter must not perturb a single value of the safety-critical ATR series.
        IReadOnlyList<Bar> bars = Series();

        new AtrIndicator(3).Compute(bars).Should().Equal(AverageTrueRange.Compute(bars, period: 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldRejectANonPositivePeriod(int period)
    {
        Action act = () => new AtrIndicator(period);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
