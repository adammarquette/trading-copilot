using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

public class VenueAccountTests
{
    private static VenueAccount Account(bool canTrade, bool isVisible)
    {
        return new VenueAccount(
            VenueAccountId.Create(VenueId.Parse("projectx"), "9001"),
            "PRAC-50K-1234",
            Balance: 50_000m,
            CanTrade: canTrade,
            IsVisible: isVisible,
            Mode: TradingMode.Practice);
    }

    [Fact]
    public void IsSelectable_ShouldBeTrue_WhenAccountCanTradeAndIsVisible()
    {
        Account(canTrade: true, isVisible: true).IsSelectable.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void IsSelectable_ShouldBeFalse_WhenAccountCannotTradeOrIsHidden(bool canTrade, bool isVisible)
    {
        Account(canTrade, isVisible).IsSelectable.Should().BeFalse();
    }
}
