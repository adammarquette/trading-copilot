using MarqSpec.TradingCopilot.Domain;

namespace MarqSpec.TradingCopilot.UnitTests.Domain;

public class PriceTests
{
    [Fact]
    public void IsOnTick_ShouldBeTrue_WhenValueLandsOnTheGrid()
    {
        new Price(5230.25m).IsOnTick(0.25m).Should().BeTrue();
    }

    [Fact]
    public void IsOnTick_ShouldBeFalse_WhenValueIsBetweenTicks()
    {
        new Price(5230.30m).IsOnTick(0.25m).Should().BeFalse();
    }

    [Fact]
    public void RoundToTick_ShouldSnapToNearestTick()
    {
        new Price(5230.30m).RoundToTick(0.25m).Value.Should().Be(5230.25m);
    }

    [Fact]
    public void RoundToTick_ShouldThrowArgumentOutOfRange_WhenTickSizeNotPositive()
    {
        Action act = () => new Price(5230m).RoundToTick(0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
