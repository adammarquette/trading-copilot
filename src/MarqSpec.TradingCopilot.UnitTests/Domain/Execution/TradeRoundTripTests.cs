using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Execution;

public class TradeRoundTripTests
{
    private static RoundTripFill Fill(OrderSide side, decimal price, int size, int minute = 0) =>
        new(side, price, size, new DateTimeOffset(2026, 8, 8, 14, minute, 0, TimeSpan.Zero));

    [Fact]
    public void TryCompose_ShouldFormTheRoundTrip_WhenOneEntryFillIsClosedByOneExitFill()
    {
        RoundTripFill[] fills = [Fill(OrderSide.Buy, 5000m, 2), Fill(OrderSide.Sell, 5010m, 2, minute: 5)];

        bool composed = TradeRoundTrip.TryCompose(fills, out RoundTrip? trip);

        composed.Should().BeTrue();
        trip!.EntrySide.Should().Be(OrderSide.Buy);
        trip.EntryPrice.Should().Be(5000m);
        trip.ExitPrice.Should().Be(5010m);
        trip.Size.Should().Be(2);
        trip.ClosedAt.Should().Be(new DateTimeOffset(2026, 8, 8, 14, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryCompose_ShouldSizeWeightTheAverages_WhenALegFillsInParts()
    {
        // Enter 3 @ 5000 and 1 @ 5008 -> size-weighted 5002; exit 2 @ 5020 and 2 @ 5010 -> 5015.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5000m, 3),
            Fill(OrderSide.Buy, 5008m, 1, minute: 1),
            Fill(OrderSide.Sell, 5020m, 2, minute: 6),
            Fill(OrderSide.Sell, 5010m, 2, minute: 7),
        ];

        TradeRoundTrip.TryCompose(fills, out RoundTrip? trip).Should().BeTrue();

        trip!.EntryPrice.Should().Be(5002m);
        trip.ExitPrice.Should().Be(5015m);
        trip.Size.Should().Be(4);
    }

    [Fact]
    public void TryCompose_ShouldOrderByExecutionTime_NotByThePositionInTheCollection()
    {
        // Handed to us newest-first: the Sell is listed before the Buy but executed 20 minutes AFTER it. The
        // entry leg is the EARLIEST execution (the Buy), never merely the first element.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Sell, 5000m, 1, minute: 30),
            Fill(OrderSide.Buy, 4990m, 1, minute: 10),
        ];

        TradeRoundTrip.TryCompose(fills, out RoundTrip? trip).Should().BeTrue();

        trip!.EntrySide.Should().Be(OrderSide.Buy);
        trip.EntryPrice.Should().Be(4990m);
        trip.ExitPrice.Should().Be(5000m);
        trip.ClosedAt.Should().Be(new DateTimeOffset(2026, 8, 8, 14, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryCompose_ShouldTakeTheEntrySideFromTheEarliestFill_WhenTheShortIsOpenedFirst()
    {
        RoundTripFill[] fills = [Fill(OrderSide.Sell, 5000m, 1), Fill(OrderSide.Buy, 4990m, 1, minute: 4)];

        TradeRoundTrip.TryCompose(fills, out RoundTrip? trip).Should().BeTrue();

        trip!.EntrySide.Should().Be(OrderSide.Sell);
        trip.EntryPrice.Should().Be(5000m);
        trip.ExitPrice.Should().Be(4990m);
    }

    [Fact]
    public void TryCompose_ShouldSumTheFees_AcrossBothLegs()
    {
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5000m, 1) with { Fees = 1.25m },
            Fill(OrderSide.Sell, 5010m, 1, minute: 5) with { Fees = 1.30m },
        ];

        TradeRoundTrip.TryCompose(fills, out RoundTrip? trip).Should().BeTrue();

        trip!.Fees.Should().Be(2.55m);
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenTheLegsDoNotBalance()
    {
        // Scale-in/partial-exit: 3 in, 2 out. Not flat, so not a round trip -- deferred, never guessed at.
        RoundTripFill[] fills = [Fill(OrderSide.Buy, 5000m, 3), Fill(OrderSide.Sell, 5010m, 2, minute: 5)];

        TradeRoundTrip.TryCompose(fills, out RoundTrip? trip).Should().BeFalse();
        trip.Should().BeNull();
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenEveryFillIsOnTheSameSide()
    {
        RoundTripFill[] fills = [Fill(OrderSide.Buy, 5000m, 1), Fill(OrderSide.Buy, 5001m, 1, minute: 2)];

        TradeRoundTrip.TryCompose(fills, out RoundTrip? trip).Should().BeFalse();
        trip.Should().BeNull();
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenThereAreNoFills()
    {
        TradeRoundTrip.TryCompose([], out RoundTrip? trip).Should().BeFalse();
        trip.Should().BeNull();
    }

    [Fact]
    public void TryCompose_ShouldThrow_WhenTheFillsAreNull()
    {
        Action act = () => TradeRoundTrip.TryCompose(null!, out _);

        act.Should().Throw<ArgumentNullException>();
    }
}
