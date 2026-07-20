using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

public class VenueAccountIdTests
{
    private static VenueId ProjectX => VenueId.Parse("projectx");

    [Fact]
    public void Create_ShouldTrimKey_WhenKeyHasWhitespace()
    {
        VenueAccountId id = VenueAccountId.Create(ProjectX, "  9001  ");

        id.Key.Should().Be("9001");
        id.Venue.Should().Be(ProjectX);
    }

    [Fact]
    public void Create_ShouldPreserveKeyCase_WhenKeyIsOpaque()
    {
        // Venue account keys are opaque venue handles -- normalizing case could break a real identifier.
        VenueAccountId.Create(ProjectX, "Acct-XY").Key.Should().Be("Acct-XY");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrowArgumentException_WhenKeyIsBlank(string? key)
    {
        Action act = () => VenueAccountId.Create(ProjectX, key!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_ShouldThrowArgumentException_WhenVenueIsDefault()
    {
        Action act = () => VenueAccountId.Create(default, "9001");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToString_ShouldQualifyKeyWithVenue()
    {
        VenueAccountId.Create(ProjectX, "9001").ToString().Should().Be("projectx:9001");
    }

    [Fact]
    public void TryParse_ShouldRoundTrip_WhenValueIsQualified()
    {
        VenueAccountId original = VenueAccountId.Create(ProjectX, "9001");

        bool parsed = VenueAccountId.TryParse(original.ToString(), out VenueAccountId result);

        parsed.Should().BeTrue();
        result.Should().Be(original);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9001")]
    [InlineData("projectx:")]
    [InlineData(":9001")]
    public void TryParse_ShouldReturnFalse_WhenValueIsNotQualified(string? value)
    {
        VenueAccountId.TryParse(value, out VenueAccountId result).Should().BeFalse();
        result.Should().Be(default(VenueAccountId));
    }

    [Fact]
    public void Equality_ShouldNotHold_WhenSameKeyBelongsToDifferentVenues()
    {
        VenueAccountId projectX = VenueAccountId.Create(ProjectX, "9001");
        VenueAccountId tradovate = VenueAccountId.Create(VenueId.Parse("tradovate"), "9001");

        projectX.Should().NotBe(tradovate);
    }
}
