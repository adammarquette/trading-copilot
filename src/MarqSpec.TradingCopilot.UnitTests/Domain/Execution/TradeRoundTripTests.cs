using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Execution;

public class TradeRoundTripTests
{
    private static RoundTripFill Fill(
        OrderSide side, decimal price, int size, int minute = 0, decimal fees = 0m, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), side, price, size, new DateTimeOffset(2026, 8, 8, 14, minute, 0, TimeSpan.Zero))
        {
            Fees = fees,
        };

    // --- The simple balanced trip (gh#731 behaviour, preserved) ---

    [Fact]
    public void TryCompose_ShouldComposeOneTrip_WhenOneEntryFillIsClosedByOneExitFill()
    {
        RoundTripFill[] fills = [Fill(OrderSide.Buy, 5000m, 2), Fill(OrderSide.Sell, 5010m, 2, minute: 5)];

        bool composed = TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips);

        composed.Should().BeTrue();
        RoundTrip trip = trips.Should().ContainSingle().Subject;
        trip.EntrySide.Should().Be(OrderSide.Buy);
        trip.EntryPrice.Should().Be(5000m);
        trip.ExitPrice.Should().Be(5010m);
        trip.Size.Should().Be(2);
        trip.ClosedAt.Should().Be(new DateTimeOffset(2026, 8, 8, 14, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryCompose_ShouldComposeOneTrip_WhenOneOpeningLegIsRetiredByTwoClosingFills()
    {
        // Acceptance criterion (gh#759): an entry of 10 exited 5 + 5 is ONE trade -- the exits are partial
        // retirements of a single leg, not separate trips. Entry price is that one opening fill's (exact); the exit
        // is the size-weighted blend of the two closes: (5010*5 + 5020*5) / 10 = 5015.
        Guid opening = Guid.NewGuid();
        Guid finalClose = Guid.NewGuid();
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5000m, 10, id: opening),
            Fill(OrderSide.Sell, 5010m, 5, minute: 5),
            Fill(OrderSide.Sell, 5020m, 5, minute: 6, id: finalClose),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeTrue();

        RoundTrip trip = trips.Should().ContainSingle().Subject;
        trip.Size.Should().Be(10);
        trip.EntryPrice.Should().Be(5000m);
        trip.ExitPrice.Should().Be(5015m);
        trip.OpeningFillId.Should().Be(opening);
        trip.ClosingFillId.Should().Be(finalClose, "the key's closing half is the fill that retired the last of the leg");
        trip.ClosedAt.Should().Be(new DateTimeOffset(2026, 8, 8, 14, 6, 0, TimeSpan.Zero));
    }

    // --- Scale-in: per-leg, FIFO (gh#759) ---

    [Fact]
    public void TryCompose_ShouldComposeTwoTrips_WhenAScaleInIsExitedLegByLeg()
    {
        // Acceptance criterion: a scale-in (5 then 5) exited 5 then 5 is TWO trades, oldest leg retired first.
        Guid firstOpen = Guid.NewGuid();
        Guid secondOpen = Guid.NewGuid();
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5000m, 5, minute: 0, id: firstOpen),
            Fill(OrderSide.Buy, 5010m, 5, minute: 1, id: secondOpen),
            Fill(OrderSide.Sell, 5030m, 5, minute: 5),
            Fill(OrderSide.Sell, 5040m, 5, minute: 6),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeTrue();

        trips.Should().HaveCount(2);
        trips[0].OpeningFillId.Should().Be(firstOpen, "FIFO retires the oldest open leg first");
        trips[0].EntryPrice.Should().Be(5000m);
        trips[0].ExitPrice.Should().Be(5030m);
        trips[1].OpeningFillId.Should().Be(secondOpen);
        trips[1].EntryPrice.Should().Be(5010m);
        trips[1].ExitPrice.Should().Be(5040m);
        trips.Should().OnlyContain(trip => trip.Size == 5 && trip.EntrySide == OrderSide.Buy);
    }

    [Fact]
    public void TryCompose_ShouldSplitASpanningClosingFill_IntoOneTripPerLeg()
    {
        // Acceptance criterion + the reason for the composite key: a scale-in (5 then 5) exited by a SINGLE 10-lot
        // fill is TWO trades via allocation -- not a refusal, not one blended row. Both share the ONE closing fill's
        // id, so `ClosingFillId` alone is no longer unique; `(ClosingFillId, OpeningFillId)` is what distinguishes them.
        Guid firstOpen = Guid.NewGuid();
        Guid secondOpen = Guid.NewGuid();
        Guid spanningClose = Guid.NewGuid();
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5000m, 5, minute: 0, id: firstOpen),
            Fill(OrderSide.Buy, 5010m, 5, minute: 1, id: secondOpen),
            Fill(OrderSide.Sell, 5030m, 10, minute: 5, id: spanningClose),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeTrue();

        trips.Should().HaveCount(2);
        trips.Should().OnlyContain(trip => trip.ClosingFillId == spanningClose, "one closing fill retired both legs");
        trips.Select(trip => trip.OpeningFillId).Should().Equal(firstOpen, secondOpen);
        trips.Should().OnlyContain(trip => trip.Size == 5 && trip.ExitPrice == 5030m);
    }

    // --- Stop-and-reverse: falls out of the same walk (gh#759) ---

    [Fact]
    public void TryCompose_ShouldComposeBothDirections_WhenAPositionStopsAndReverses()
    {
        // Acceptance criterion: a stop-and-reverse produces the closing leg's trade then opens the opposite -- no
        // special-casing. Buy 5 (long), Sell 10 (retires the long AND opens a short 5), Buy 5 (retires the short).
        // The Sell 10 is BOTH the closing fill of the long leg and the opening fill of the short leg.
        Guid longOpen = Guid.NewGuid();
        Guid reversing = Guid.NewGuid();
        Guid shortClose = Guid.NewGuid();
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5000m, 5, minute: 0, id: longOpen),
            Fill(OrderSide.Sell, 5010m, 10, minute: 5, id: reversing),
            Fill(OrderSide.Buy, 4990m, 5, minute: 10, id: shortClose),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeTrue();

        trips.Should().HaveCount(2);

        RoundTrip longLeg = trips[0];
        longLeg.EntrySide.Should().Be(OrderSide.Buy);
        longLeg.OpeningFillId.Should().Be(longOpen);
        longLeg.ClosingFillId.Should().Be(reversing, "the reversing fill retired the long leg");
        longLeg.EntryPrice.Should().Be(5000m);
        longLeg.ExitPrice.Should().Be(5010m);

        RoundTrip shortLeg = trips[1];
        shortLeg.EntrySide.Should().Be(OrderSide.Sell);
        shortLeg.OpeningFillId.Should().Be(reversing, "the same reversing fill opened the short leg");
        shortLeg.ClosingFillId.Should().Be(shortClose);
        shortLeg.EntryPrice.Should().Be(5010m);
        shortLeg.ExitPrice.Should().Be(4990m);
        shortLeg.Size.Should().Be(5);
    }

    [Fact]
    public void TryCompose_ShouldComposeTwoTrips_WhenTwoRoundTripsShareTheWindow()
    {
        // Was refused by gh#731 (exposure returned to flat before the end). Under FIFO it is simply two completed
        // trips -- Buy 1/Sell 1, then Buy 1/Sell 1 -- each with its own entry and exit, never blended into one row.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5000m, 1, minute: 0),
            Fill(OrderSide.Sell, 5010m, 1, minute: 5),
            Fill(OrderSide.Buy, 5020m, 1, minute: 10),
            Fill(OrderSide.Sell, 5030m, 1, minute: 15),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeTrue();

        trips.Should().HaveCount(2);
        trips[0].EntryPrice.Should().Be(5000m);
        trips[0].ExitPrice.Should().Be(5010m);
        trips[1].EntryPrice.Should().Be(5020m);
        trips[1].ExitPrice.Should().Be(5030m);
    }

    // --- Prices and fees ---

    [Fact]
    public void TryCompose_ShouldWeightOnlyTheExit_WhenOneLegIsRetiredInParts()
    {
        // A single opening fill (exact entry) retired by two closes -> the ENTRY is not blended (it is one fill), only
        // the exit is size-weighted: (5020*2 + 5010*2) / 4 = 5015.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5002m, 4),
            Fill(OrderSide.Sell, 5020m, 2, minute: 6),
            Fill(OrderSide.Sell, 5010m, 2, minute: 7),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeTrue();

        RoundTrip trip = trips.Should().ContainSingle().Subject;
        trip.EntryPrice.Should().Be(5002m, "a leg's entry is its one opening fill's price, never blended");
        trip.ExitPrice.Should().Be(5015m);
        trip.Size.Should().Be(4);
    }

    [Fact]
    public void TryCompose_ShouldSumTheFees_AcrossBothLegsOfASimpleTrip()
    {
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5000m, 1, fees: 1.25m),
            Fill(OrderSide.Sell, 5010m, 1, minute: 5, fees: 1.30m),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeTrue();

        trips.Should().ContainSingle().Subject.Fees.Should().Be(2.55m);
    }

    [Fact]
    public void TryCompose_ShouldAllocateASpanningClosingFillsFeesBySize()
    {
        // A 10-lot close with $2.00 fees splits 5/10 + 5/10 across the two legs it retires; each leg also keeps its
        // own opening fill's fee. So each trip = $0.50 (opening) + $1.00 (half the close) = $1.50, and the two sum to
        // exactly the fees actually charged (2 * 0.50 + 2.00 = 3.00) -- fees are conserved, never invented or dropped.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5000m, 5, minute: 0, fees: 0.50m),
            Fill(OrderSide.Buy, 5010m, 5, minute: 1, fees: 0.50m),
            Fill(OrderSide.Sell, 5030m, 10, minute: 5, fees: 2.00m),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeTrue();

        trips.Should().OnlyContain(trip => trip.Fees == 1.50m);
        trips.Sum(trip => trip.Fees).Should().Be(3.00m, "every fee charged is attributed to exactly one leg, none lost");
    }

    // --- Ordering ---

    [Fact]
    public void TryCompose_ShouldOrderByExecutionTime_NotByThePositionInTheCollection()
    {
        // Handed to us newest-first: the Sell is listed before the Buy but executed 20 minutes AFTER it. The entry
        // leg is the EARLIEST execution (the Buy), never merely the first element.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Sell, 5000m, 1, minute: 30),
            Fill(OrderSide.Buy, 4990m, 1, minute: 10),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeTrue();

        RoundTrip trip = trips.Should().ContainSingle().Subject;
        trip.EntrySide.Should().Be(OrderSide.Buy);
        trip.EntryPrice.Should().Be(4990m);
        trip.ExitPrice.Should().Be(5000m);
    }

    [Fact]
    public void TryCompose_ShouldTakeTheEntrySideFromTheEarliestFill_WhenTheShortIsOpenedFirst()
    {
        RoundTripFill[] fills = [Fill(OrderSide.Sell, 5000m, 1), Fill(OrderSide.Buy, 4990m, 1, minute: 4)];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeTrue();

        RoundTrip trip = trips.Should().ContainSingle().Subject;
        trip.EntrySide.Should().Be(OrderSide.Sell);
        trip.EntryPrice.Should().Be(5000m);
        trip.ExitPrice.Should().Be(4990m);
    }

    // --- Refuse-don't-guess (genuine ambiguity only) ---

    [Fact]
    public void TryCompose_ShouldRefuse_WhenTheWindowDoesNotReturnToFlat()
    {
        // Scale-in with no full exit: 3 in, 2 out, still holding 1. Leftover open exposure is not a completed trip --
        // the writer only composes at a flat, so a non-flat window is a bug or a straddle. Refuse the whole window.
        RoundTripFill[] fills = [Fill(OrderSide.Buy, 5000m, 3), Fill(OrderSide.Sell, 5010m, 2, minute: 5)];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeFalse();
        trips.Should().BeEmpty();
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenEveryFillIsOnTheSameSide()
    {
        RoundTripFill[] fills = [Fill(OrderSide.Buy, 5000m, 1), Fill(OrderSide.Buy, 5001m, 1, minute: 2)];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeFalse();
        trips.Should().BeEmpty();
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenThereAreNoFills()
    {
        TradeRoundTrip.TryCompose([], out IReadOnlyList<RoundTrip> trips).Should().BeFalse();
        trips.Should().BeEmpty();
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
        // An undefined OrderSide (a bad cast or deserialize -- Order.Side carries no known-value DB check) cannot be
        // classified as an open or a close; refuse the whole window rather than compose a trip with a corrupt leg.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5_000m, 2),
            Fill((OrderSide)99, 5_010m, 1, minute: 5),
            Fill(OrderSide.Sell, 5_020m, 1, minute: 10),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeFalse();
        trips.Should().BeEmpty();
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenOppositeSideFillsShareTheOpeningInstant()
    {
        // The direction of a leg opened from flat must be decidable. When opposite-side fills tie on the opening
        // instant, there is no venue sequence here to say which opened first, and the choice flips the trip's sign.
        // [Buy@t, Sell@t] and [Sell@t, Buy@t] must BOTH refuse -- input order must never decide the sign of realized P&L.
        RoundTripFill[] buyFirst = [Fill(OrderSide.Buy, 5_000m, 1), Fill(OrderSide.Sell, 5_010m, 1)];
        RoundTripFill[] sellFirst = [Fill(OrderSide.Sell, 5_010m, 1), Fill(OrderSide.Buy, 5_000m, 1)];

        TradeRoundTrip.TryCompose(buyFirst, out IReadOnlyList<RoundTrip> a).Should().BeFalse();
        a.Should().BeEmpty();

        TradeRoundTrip.TryCompose(sellFirst, out IReadOnlyList<RoundTrip> b).Should().BeFalse();
        b.Should().BeEmpty();
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenOppositeSideFillsShareAMidCycleInstant()
    {
        // gh#759 review. The ambiguity is NOT only at an open-from-flat. Here a Sell and a Buy share an instant
        // mid-cycle (net != 0): depending on their arbitrary order the Sell reverses into a SHORT (excess opens the
        // opposite way) OR the Buy scales first and the Sell just closes -- flipping the second leg's SIDE and sign
        // for the same executions. Refuse rather than let the FillId tie-break silently pick a direction.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5_000m, 10, minute: 0),
            Fill(OrderSide.Sell, 5_010m, 15, minute: 5),
            Fill(OrderSide.Buy, 4_990m, 5, minute: 5), // SAME instant as the sell, opposite side
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeFalse();
        trips.Should().BeEmpty();
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenOppositeSideFillsShareAFlatBoundaryInstant()
    {
        // gh#759 review. Two trips where the first's close and the second's open share an instant: their order decides
        // whether it is "close then reopen" (two trips) or "scale then close" (one) -- opposite compositions. Refuse.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5_000m, 1, minute: 0),
            Fill(OrderSide.Sell, 5_010m, 1, minute: 5),
            Fill(OrderSide.Buy, 5_020m, 1, minute: 5), // SAME instant as the sell above, opposite side
            Fill(OrderSide.Sell, 5_030m, 1, minute: 10),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeFalse();
        trips.Should().BeEmpty();
    }

    [Fact]
    public void TryCompose_ShouldRefuse_WhenTheEarliestFillHasAnUnknownSide()
    {
        RoundTripFill[] fills =
        [
            Fill((OrderSide)99, 5_000m, 1),
            Fill(OrderSide.Sell, 5_010m, 1, minute: 5),
        ];

        TradeRoundTrip.TryCompose(fills, out IReadOnlyList<RoundTrip> trips).Should().BeFalse();
        trips.Should().BeEmpty();
    }

    // --- Compose: the Composed / StillOpen / Ambiguous discriminator (gh#748) ---
    // TryCompose collapses four different refusals to a bare `false`. Compose keeps the one distinction the caller
    // needs: a window that does not reconcile because a closing fill is MISSING or not yet ingested is StillOpen
    // (retryable -- composing again once the fill lands completes the trip), while an undefined side or a same-instant
    // opposite-side tie is Ambiguous (terminal -- retrying cannot resolve it). The two ambiguity guards must stay
    // before the FIFO walk so Ambiguous is distinguishable from StillOpen.

    [Fact]
    public void Compose_ShouldReturnComposed_WhenTheFillsFormABalancedTrip()
    {
        RoundTripFill[] fills = [Fill(OrderSide.Buy, 5000m, 2), Fill(OrderSide.Sell, 5010m, 2, minute: 5)];

        RoundTripComposition outcome = TradeRoundTrip.Compose(fills, out IReadOnlyList<RoundTrip> trips);

        outcome.Should().Be(RoundTripComposition.Composed);
        trips.Should().ContainSingle();
    }

    [Fact]
    public void Compose_ShouldReturnStillOpen_WhenAnOpenLegHasNoClosingFillYet()
    {
        // 3 in, 2 out -- one lot is still open because its closing fill is missing or not yet ingested. Retryable:
        // once the fill lands, composing again completes the trip (gh#748), never the terminal refuse-don't-guess.
        RoundTripFill[] fills = [Fill(OrderSide.Buy, 5000m, 3), Fill(OrderSide.Sell, 5010m, 2, minute: 5)];

        RoundTripComposition outcome = TradeRoundTrip.Compose(fills, out IReadOnlyList<RoundTrip> trips);

        outcome.Should().Be(RoundTripComposition.StillOpen);
        trips.Should().BeEmpty();
    }

    [Fact]
    public void Compose_ShouldReturnStillOpen_WhenThereAreNoFillsYet()
    {
        // No fills yet is the extreme of a not-yet-reconciled window -- retryable, not ambiguous.
        RoundTripComposition outcome = TradeRoundTrip.Compose([], out IReadOnlyList<RoundTrip> trips);

        outcome.Should().Be(RoundTripComposition.StillOpen);
        trips.Should().BeEmpty();
    }

    [Fact]
    public void Compose_ShouldReturnAmbiguous_WhenAFillHasAnUnknownSide()
    {
        // An undefined side cannot be classified as an open or a close -- terminal ambiguity, never a missing fill.
        RoundTripFill[] fills =
        [
            Fill(OrderSide.Buy, 5_000m, 2),
            Fill((OrderSide)99, 5_010m, 1, minute: 5),
            Fill(OrderSide.Sell, 5_020m, 1, minute: 10),
        ];

        RoundTripComposition outcome = TradeRoundTrip.Compose(fills, out IReadOnlyList<RoundTrip> trips);

        outcome.Should().Be(RoundTripComposition.Ambiguous);
        trips.Should().BeEmpty();
    }

    [Fact]
    public void Compose_ShouldReturnAmbiguous_WhenOppositeSideFillsShareAnInstant()
    {
        // A same-instant opposite-side tie is decidable only by an arbitrary tiebreak that flips the sign (ADR-0022) --
        // terminal ambiguity. A later fill cannot resolve it, so it must NOT read as StillOpen and be retried forever.
        RoundTripFill[] fills = [Fill(OrderSide.Buy, 5_000m, 1), Fill(OrderSide.Sell, 5_010m, 1)];

        RoundTripComposition outcome = TradeRoundTrip.Compose(fills, out IReadOnlyList<RoundTrip> trips);

        outcome.Should().Be(RoundTripComposition.Ambiguous);
        trips.Should().BeEmpty();
    }
}
