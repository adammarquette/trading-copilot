using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

public class VenueCapabilitiesTests
{
    [Fact]
    public void Supports_ShouldReturnTrue_WhenCapabilityIsGranted()
    {
        VenueCapabilities capabilities = VenueCapabilities.Of(VenueCapability.HistoricalBars | VenueCapability.Quotes);

        capabilities.Supports(VenueCapability.Quotes).Should().BeTrue();
    }

    [Fact]
    public void Supports_ShouldReturnFalse_WhenCapabilityIsAbsent()
    {
        VenueCapabilities capabilities = VenueCapabilities.Of(VenueCapability.HistoricalBars);

        capabilities.Supports(VenueCapability.MarketDepth).Should().BeFalse();
    }

    [Fact]
    public void Supports_ShouldRequireEveryFlag_WhenCapabilityIsComposite()
    {
        VenueCapabilities capabilities = VenueCapabilities.Of(VenueCapability.HistoricalBars);

        capabilities.Supports(VenueCapability.HistoricalBars | VenueCapability.MarketDepth).Should().BeFalse();
    }

    [Fact]
    public void Supports_ShouldReturnFalse_WhenNothingIsGranted()
    {
        VenueCapabilities.None.Supports(VenueCapability.Quotes).Should().BeFalse();
    }

    [Fact]
    public void Require_ShouldThrowNamingTheCapability_WhenCapabilityIsAbsent()
    {
        VenueCapabilities capabilities = VenueCapabilities.Of(VenueCapability.Quotes);

        Action act = () => capabilities.Require(VenueCapability.BracketOrders);

        act.Should().Throw<VenueCapabilityNotSupportedException>()
            .Which.Message.Should().Contain(nameof(VenueCapability.BracketOrders));
    }

    [Fact]
    public void Require_ShouldNotThrow_WhenCapabilityIsGranted()
    {
        VenueCapabilities capabilities = VenueCapabilities.Of(VenueCapability.ClosePosition);

        Action act = () => capabilities.Require(VenueCapability.ClosePosition);

        act.Should().NotThrow();
    }

    [Fact]
    public void MissingCapability_ShouldBeExposed_WhenRequireThrows()
    {
        VenueCapabilities capabilities = VenueCapabilities.Of(VenueCapability.Quotes);

        Action act = () => capabilities.Require(VenueCapability.TrailingStops);

        act.Should().Throw<VenueCapabilityNotSupportedException>()
            .Which.MissingCapability.Should().Be(VenueCapability.TrailingStops);
    }
}
