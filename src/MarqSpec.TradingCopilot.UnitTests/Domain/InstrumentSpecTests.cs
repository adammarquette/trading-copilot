using MarqSpec.TradingCopilot.Domain;

namespace MarqSpec.TradingCopilot.UnitTests.Domain;

public class InstrumentSpecTests
{
    private static InstrumentSpec Es()
    {
        return InstrumentSpec.Create(InstrumentId.Parse("ES"), tickSize: 0.25m, pointValue: 50m);
    }

    [Fact]
    public void Create_ShouldExposeTheInstrumentsMoneyMath()
    {
        InstrumentSpec es = Es();

        es.Id.Should().Be(InstrumentId.Parse("ES"));
        es.TickSize.Should().Be(0.25m);
        es.PointValue.Should().Be(50m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.25)]
    public void Create_ShouldThrow_WhenTickSizeIsNotPositive(decimal tickSize)
    {
        Action act = () => InstrumentSpec.Create(InstrumentId.Parse("ES"), tickSize, pointValue: 50m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void Create_ShouldThrow_WhenPointValueIsNotPositive(decimal pointValue)
    {
        Action act = () => InstrumentSpec.Create(InstrumentId.Parse("ES"), tickSize: 0.25m, pointValue);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LossPerContract_ShouldPriceTheDistanceInMoney_WhenLong()
    {
        // ES at $50/point: a 5-point stop is $250 per contract.
        Es().LossPerContract(new Price(5_000m), new Price(4_995m)).Should().Be(250m);
    }

    [Fact]
    public void LossPerContract_ShouldBeDirectionAgnostic_WhenShort()
    {
        Es().LossPerContract(new Price(4_995m), new Price(5_000m)).Should().Be(250m);
    }

    [Fact]
    public void LossPerContract_ShouldBeZero_WhenExitEqualsEntry()
    {
        Es().LossPerContract(new Price(5_000m), new Price(5_000m)).Should().Be(0m);
    }
}
