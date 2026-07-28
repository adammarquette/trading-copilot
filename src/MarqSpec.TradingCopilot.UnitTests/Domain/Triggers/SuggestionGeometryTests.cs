using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Triggers;

/// <summary>
/// The pure suggestion-geometry sanity check (R-4 / ADR-0008) — the layer that rejects a self-evidently broken
/// proposed setup (non-positive price, or a stop/target on the wrong side of entry) before it can be persisted.
/// </summary>
public class SuggestionGeometryTests
{
    [Fact]
    public void Validate_ShouldAccept_ACoherentLong()
    {
        // A long risks below entry and targets above it.
        SuggestionGeometry.Validate(OrderSide.Buy, entry: 5000m, stop: 4990m, target: 5020m).Should().BeNull();
    }

    [Fact]
    public void Validate_ShouldAccept_ACoherentShort()
    {
        // A short is the mirror: target below, stop above.
        SuggestionGeometry.Validate(OrderSide.Sell, entry: 5000m, stop: 5010m, target: 4980m).Should().BeNull();
    }

    [Theory]
    [InlineData(5000, 5010, 5020)] // stop above entry -- wrong side for a long
    [InlineData(5000, 4990, 4980)] // target below entry -- wrong side for a long
    public void Validate_ShouldReject_AWrongSidedLong(decimal entry, decimal stop, decimal target)
    {
        SuggestionGeometry.Validate(OrderSide.Buy, entry, stop, target).Should().NotBeNull();
    }

    [Theory]
    [InlineData(5000, 4980, 5010)] // stop below entry -- wrong side for a short
    [InlineData(5000, 5010, 5020)] // target above entry -- wrong side for a short
    public void Validate_ShouldReject_AWrongSidedShort(decimal entry, decimal stop, decimal target)
    {
        SuggestionGeometry.Validate(OrderSide.Sell, entry, stop, target).Should().NotBeNull();
    }

    [Theory]
    [InlineData(0, 4990, 5020)]
    [InlineData(5000, 0, 5020)]
    [InlineData(5000, 4990, 0)]
    [InlineData(5000, -1, 5020)]
    public void Validate_ShouldReject_ANonPositivePrice(decimal entry, decimal stop, decimal target)
    {
        SuggestionGeometry.Validate(OrderSide.Buy, entry, stop, target).Should().NotBeNull();
    }
}
