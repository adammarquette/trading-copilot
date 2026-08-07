using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;

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

    // RealizedPnL — the SIGNED, direction-aware money a round trip made or lost (gh#731, the journal's P&L). Unlike
    // LossPerContract (absolute, for sizing), a long profits above entry and a short below it.

    [Fact]
    public void RealizedPnL_ShouldBePositive_WhenALongExitsAboveEntry() =>
        // ES at $50/point: a 2-lot long 5000 -> 5010 is +10pt * 2 * 50 = +$1,000.
        Es().RealizedPnL(new Price(5_000m), new Price(5_010m), OrderSide.Buy, size: 2).Should().Be(1_000m);

    [Fact]
    public void RealizedPnL_ShouldBeNegative_WhenALongExitsBelowEntry() =>
        Es().RealizedPnL(new Price(5_000m), new Price(4_995m), OrderSide.Buy, size: 1).Should().Be(-250m);

    [Fact]
    public void RealizedPnL_ShouldBePositive_WhenAShortExitsBelowEntry() =>
        // A short's sign is the mirror of a long's: profit when the exit is below the entry.
        Es().RealizedPnL(new Price(5_010m), new Price(5_000m), OrderSide.Sell, size: 2).Should().Be(1_000m);

    [Fact]
    public void RealizedPnL_ShouldBeNegative_WhenAShortExitsAboveEntry() =>
        Es().RealizedPnL(new Price(5_000m), new Price(5_005m), OrderSide.Sell, size: 1).Should().Be(-250m);

    [Fact]
    public void RealizedPnL_ShouldBeZero_WhenExitEqualsEntry() =>
        Es().RealizedPnL(new Price(5_000m), new Price(5_000m), OrderSide.Buy, size: 3).Should().Be(0m);

    [Fact]
    public void RealizedPnL_ShouldScaleWithSize() =>
        Es().RealizedPnL(new Price(5_000m), new Price(5_001m), OrderSide.Buy, size: 4).Should().Be(200m);

    [Fact]
    public void RealizedPnL_ShouldStayExactInDecimal_NeverRoundingMoney() =>
        // A quarter-point on ES is $12.50 — the fractional cent must survive (decimal, not float).
        Es().RealizedPnL(new Price(5_000m), new Price(5_000.25m), OrderSide.Buy, size: 1).Should().Be(12.5m);

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void RealizedPnL_ShouldThrow_WhenSizeIsNotPositive(int size)
    {
        Action act = () => Es().RealizedPnL(new Price(5_000m), new Price(5_010m), OrderSide.Buy, size);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
