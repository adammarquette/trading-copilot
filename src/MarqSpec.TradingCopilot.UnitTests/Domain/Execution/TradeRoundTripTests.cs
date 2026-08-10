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
    public void TryCompose_ShouldRefuse_WhenExposureCrossesThroughFlatIntoAReversal()
    {
        // gh#734 review. Equal TOTALS do not prove one enter -> exit -> flat trip. Buy 1, Sell 2, Buy 1 sums to
        // 2 buys and 2 sells, so a totals-only balance check accepts it as a single size-2 long — but exposure went
        // +1, then -1 (a SHORT), then 0. That is a stop-and-reverse: two positions in opposite directions, whose
        // blended "average entry" and "average exit" describe neither. The money journalled from it would be
        // fiction, and this composer's whole contract is to refuse anything that is not the simple trip.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5_000m, 1),
            Fill(OrderSide.Sell, 5_010m, 2, minute: 5),
            Fill(OrderSide.Buy, 5_020m, 1, minute: 10),
        ];

        bool composed = TradeRoundTrip.TryCompose(fills, out RoundTrip? trip);

        composed.Should().BeFalse(
            "exposure reached flat and crossed into a short before the final fill — that is a reversal, not the "
            + "single round trip this composes");
        trip.Should().BeNull();
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenThePositionClosesAndReopensBeforeTheFinalFill()
    {
        // The gentler sibling of the reversal above, and the one a totals check is likeliest to wave through: a
        // completed trip followed by a second entry that has not closed yet. Buy 1, Sell 1, Buy 1, Sell 1 balances
        // 2-vs-2 and would compose as one size-2 long spanning both — blending two separate trades into a single
        // journal row with an average that matches neither. Exposure must reach zero exactly ONCE, at the end.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5_000m, 1),
            Fill(OrderSide.Sell, 5_010m, 1, minute: 5),
            Fill(OrderSide.Buy, 5_020m, 1, minute: 10),
            Fill(OrderSide.Sell, 5_030m, 1, minute: 15),
        ];

        TradeRoundTrip.TryCompose(fills, out RoundTrip? trip).Should().BeFalse(
            "exposure returned to flat at the second fill, so these are TWO round trips — composing them as one "
            + "would journal a blended entry and exit that neither trade actually had");
        trip.Should().BeNull();
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

    [Fact]
    public void TryCompose_ShouldRefuse_WhenAFillHasAnUnknownSide()
    {
        // gh#734 review. `Side != entrySide` was a blacklist, so an undefined OrderSide (a bad cast or deserialize —
        // and Order.Side carries no known-value DB check) was silently bucketed as an exit. Buy 2, (OrderSide)99
        // size 1, Sell 1 then "balances" 2-vs-2 and journals an exit average containing the invalid fill;
        // RealizedPnL only validates the known entry side and never catches it. Whitelist both legs and refuse
        // anything else before composing.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5_000m, 2),
            Fill((OrderSide)99, 5_010m, 1, minute: 5),
            Fill(OrderSide.Sell, 5_020m, 1, minute: 10),
        ];

        TradeRoundTrip.TryCompose(fills, out RoundTrip? trip).Should().BeFalse(
            "a fill whose side is neither leg cannot be classified, so the whole trip is refused rather than "
            + "journalled with a corrupt average");
        trip.Should().BeNull();
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenTheEarliestFillHasAnUnknownSide()
    {
        // The entry side is taken from the earliest execution; if THAT is an undefined value the trip cannot be
        // composed at all, rather than defaulting to a side.
        RoundTripFill[] fills =
        [
            Fill((OrderSide)99, 5_000m, 1),
            Fill(OrderSide.Sell, 5_010m, 1, minute: 5),
        ];

        TradeRoundTrip.TryCompose(fills, out RoundTrip? trip).Should().BeFalse();
        trip.Should().BeNull();
    }
}
