using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

/// <summary>
/// The protection census (gh#370, ADR-0019, R-11): how many positions are open, and how many of those have
/// <b>no protective stop resting at the venue</b>.
/// </summary>
/// <remarks>
/// The second number is the one ADR-0019 makes a P1. A live position with no exchange-held stop is the state
/// the whole staged-stop model exists to prevent, and until now nothing measured it — the nearest metric,
/// <c>trading.stops.orphaned</c>, counts a different thing (a stop that <i>was</i> native and got orphaned).
/// </remarks>
public class PositionProtectionTests
{
    private static VenueId Venue => VenueId.Parse("projectx");

    private static VenueAccountId Account => VenueAccountId.Create(Venue, "9001");

    private static VenueContractId Contract(string key) => VenueContractId.Create(Venue, key);

    private static PositionSnapshot Position(string key, int net) =>
        new(Account, Contract(key), net, new Price(5_000m));

    private static WorkingOrder Stop(string key) =>
        new("ORD-1", Contract(key), new Price(4_990m), LimitPrice: null);

    private static WorkingOrder TakeProfit(string key) =>
        new("ORD-2", Contract(key), StopPrice: null, LimitPrice: new Price(5_050m));

    [Fact]
    public void Census_ShouldCountAProtectedPosition_AsOpenButNotUnprotected()
    {
        PositionProtection.Census([Position("MESU26", 2)], [Stop("MESU26")])
            .Should().Be(new ProtectionCensus(Open: 1, Unprotected: 0));
    }

    [Fact]
    public void Census_ShouldCountAPositionWithNoRestingStop_AsUnprotected()
    {
        // The P1. A live position and nothing at the exchange to close it.
        PositionProtection.Census([Position("MESU26", 2)], [])
            .Should().Be(new ProtectionCensus(Open: 1, Unprotected: 1));
    }

    [Fact]
    public void Census_ShouldNotCountATakeProfit_AsProtection()
    {
        // A take-profit rests on the WINNING side. It caps the gain; it does nothing about the loss, so a
        // position holding only one of those is exactly as unprotected as one holding nothing.
        PositionProtection.Census([Position("MESU26", 2)], [TakeProfit("MESU26")])
            .Should().Be(new ProtectionCensus(Open: 1, Unprotected: 1));
    }

    [Fact]
    public void Census_ShouldNotCreditAStopOnADifferentContract()
    {
        // A stop resting on NQ protects nothing about an MES position. Matching on the account alone -- or
        // counting stops globally against positions globally -- would report a protected fleet while one
        // instrument sat naked.
        PositionProtection.Census([Position("MESU26", 2)], [Stop("NQU26")])
            .Should().Be(new ProtectionCensus(Open: 1, Unprotected: 1));
    }

    [Fact]
    public void Census_ShouldIgnoreAFlatPosition()
    {
        // The venue reports flat rows. Counting them as open would make an idle account look exposed, and an
        // unprotected-position alert that fires on a flat book is one nobody keeps listening to.
        PositionProtection.Census([Position("MESU26", 0)], [])
            .Should().Be(new ProtectionCensus(Open: 0, Unprotected: 0));
    }

    [Fact]
    public void Census_ShouldCountAShortPosition_LikeALongOne()
    {
        PositionProtection.Census([Position("MESU26", -2)], [])
            .Should().Be(new ProtectionCensus(Open: 1, Unprotected: 1));
    }

    [Fact]
    public void Census_ShouldCountEachContractIndependently()
    {
        ProtectionCensus census = PositionProtection.Census(
            [Position("MESU26", 2), Position("NQU26", -1), Position("CLU26", 3)],
            [Stop("NQU26")]);

        census.Should().Be(new ProtectionCensus(Open: 3, Unprotected: 2));
    }

    [Fact]
    public void Census_ShouldBeEmpty_WhenThereAreNoPositions()
    {
        PositionProtection.Census([], [Stop("MESU26")])
            .Should().Be(new ProtectionCensus(Open: 0, Unprotected: 0));
    }
}
