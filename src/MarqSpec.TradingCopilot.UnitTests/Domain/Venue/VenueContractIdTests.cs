using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

public class VenueContractIdTests
{
    private static VenueId ProjectX => VenueId.Parse("projectx");

    [Fact]
    public void Create_ShouldPreserveKeyCase_WhenKeyIsAVenueContractHandle()
    {
        // e.g. ProjectX "CON.F.US.EP.M25" -- an opaque handle the adapter resolves from an InstrumentId.
        VenueContractId id = VenueContractId.Create(ProjectX, " CON.F.US.EP.M25 ");

        id.Key.Should().Be("CON.F.US.EP.M25");
        id.Venue.Should().Be(ProjectX);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrowArgumentException_WhenKeyIsBlank(string? key)
    {
        Action act = () => VenueContractId.Create(ProjectX, key!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_ShouldThrowArgumentException_WhenVenueIsDefault()
    {
        Action act = () => VenueContractId.Create(default, "CON.F.US.EP.M25");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToString_ShouldQualifyKeyWithVenue()
    {
        VenueContractId.Create(ProjectX, "CON.F.US.EP.M25").ToString().Should().Be("projectx:CON.F.US.EP.M25");
    }

    [Fact]
    public void Equality_ShouldNotHold_WhenSameKeyBelongsToDifferentVenues()
    {
        VenueContractId.Create(ProjectX, "ESM25")
            .Should().NotBe(VenueContractId.Create(VenueId.Parse("tradovate"), "ESM25"));
    }
}
