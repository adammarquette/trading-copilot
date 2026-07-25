using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Flatten;

/// <summary>
/// The settlement / maintenance window and mark basis (gh#193, R-13/R-19, ADR-0013): a position carried through
/// the window is settlement-marked (not live), and an unreachable venue is declared-unknown (never a stale live
/// number). The window is derived from the instrument's own session close in market wall-clock time.
/// </summary>
public class MarketSessionTests
{
    // A fixed winter date (CST — no DST ambiguity) at the given Central wall-clock time, tagged with the market's
    // own offset so MarketClock converts it back exactly.
    private static DateTimeOffset CentralTime(int hour, int minute)
    {
        DateTime local = new(2026, 1, 15, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, MarketClock.CentralTime.GetUtcOffset(local));
    }

    private static FlattenSchedule EsClosingAt16() =>
        FlattenSchedule.Create(InstrumentId.Parse("ES"), enabled: true, deadlineOverride: null, sessionClose: new TimeOnly(16, 0));

    [Theory]
    [InlineData(15, 59, false)] // a minute before the 16:00 close
    [InlineData(16, 0, true)]   // at the close — the window opens
    [InlineData(16, 30, true)]  // inside
    [InlineData(16, 59, true)]  // last minute
    [InlineData(17, 0, false)]  // window closed (its end is exclusive — the 1-hour maintenance is over)
    [InlineData(9, 0, false)]   // mid-morning — nowhere near
    public void IsInSettlementWindow_ShouldSpanCloseToClosePlusMaintenance(int hour, int minute, bool expected)
    {
        MarketSession.IsInSettlementWindow(EsClosingAt16(), CentralTime(hour, minute)).Should().Be(expected);
    }

    [Fact]
    public void MarkBasisAt_ShouldBeSettlement_InsideTheWindow()
    {
        MarketSession.MarkBasisAt(EsClosingAt16(), CentralTime(16, 30), venueReachable: true)
            .Should().Be(PositionMarkBasis.Settlement);
    }

    [Fact]
    public void MarkBasisAt_ShouldBeLive_DuringTheTradingSession()
    {
        MarketSession.MarkBasisAt(EsClosingAt16(), CentralTime(9, 0), venueReachable: true)
            .Should().Be(PositionMarkBasis.Live);
    }

    [Fact]
    public void MarkBasisAt_ShouldBeUnknown_WhenVenueIsUnreachable_EvenInLiveHours()
    {
        // Fail-safe: truth could not be obtained, so it is declared unknown rather than shown as a live mark.
        MarketSession.MarkBasisAt(EsClosingAt16(), CentralTime(9, 0), venueReachable: false)
            .Should().Be(PositionMarkBasis.Unknown);
    }
}
