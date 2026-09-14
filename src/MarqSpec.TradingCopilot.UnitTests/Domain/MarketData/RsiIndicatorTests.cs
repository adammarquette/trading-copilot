using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.MarketData;

/// <summary>
/// The <see cref="RsiIndicator"/> adapter (R-22) — a faithful, thin forward to the <see cref="RelativeStrengthIndex"/>
/// math.
/// </summary>
public class RsiIndicatorTests
{
    private static DateTimeOffset Open(int minute) => new(2026, 7, 20, 14, minute, 0, TimeSpan.Zero);

    private static Bar At(int minute, decimal close) =>
        new(Open(minute), new Price(close), new Price(close), new Price(close), new Price(close), 100);

    private static IReadOnlyList<Bar> Series() => [At(0, 100), At(1, 110), At(2, 105), At(3, 110), At(4, 100)];

    [Fact]
    public void Name_ShouldBeTheStoredRsiKey()
    {
        new RsiIndicator(14).Name.Should().Be("rsi");
    }

    [Fact]
    public void Period_ShouldEchoTheConstructorArgument()
    {
        new RsiIndicator(9).Period.Should().Be(9);
    }

    [Fact]
    public void Compute_ShouldForwardVerbatimToTheRelativeStrengthIndexStatic()
    {
        IReadOnlyList<Bar> bars = Series();

        new RsiIndicator(3).Compute(bars).Should().Equal(RelativeStrengthIndex.Compute(bars, period: 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldRejectANonPositivePeriod(int period)
    {
        Action act = () => new RsiIndicator(period);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
