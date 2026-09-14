using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

public class PositionSnapshotTests
{
    private static PositionSnapshot Position(int netQuantity)
    {
        VenueId venue = VenueId.Parse("projectx");

        return new PositionSnapshot(
            VenueAccountId.Create(venue, "9001"),
            VenueContractId.Create(venue, "CON.F.US.EP.M25"),
            netQuantity,
            new Price(5000.25m));
    }

    [Fact]
    public void IsFlat_ShouldBeTrue_WhenNetQuantityIsZero()
    {
        PositionSnapshot position = Position(0);

        position.IsFlat.Should().BeTrue();
        position.IsLong.Should().BeFalse();
        position.IsShort.Should().BeFalse();
    }

    [Fact]
    public void IsLong_ShouldBeTrue_WhenNetQuantityIsPositive()
    {
        PositionSnapshot position = Position(2);

        position.IsLong.Should().BeTrue();
        position.IsFlat.Should().BeFalse();
    }

    [Fact]
    public void IsShort_ShouldBeTrue_WhenNetQuantityIsNegative()
    {
        PositionSnapshot position = Position(-3);

        position.IsShort.Should().BeTrue();
        position.IsFlat.Should().BeFalse();
    }
}
