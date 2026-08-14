using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// One execution feeding a round trip — venue facts only (gh#731). The composition below reads nothing else, so
/// it stays a pure function over fills and can be tested without a database or a venue.
/// </summary>
/// <param name="FillId">The venue fill this execution is — the identity a composed leg carries (gh#759).</param>
/// <param name="Side">The side this execution traded.</param>
/// <param name="Price">The execution price.</param>
/// <param name="Size">The executed size in contracts; positive.</param>
/// <param name="ExecutedAt">When the execution happened.</param>
public readonly record struct RoundTripFill(Guid FillId, OrderSide Side, decimal Price, int Size, DateTimeOffset ExecutedAt)
{
    /// <summary>Fees and commissions attributed to this execution.</summary>
    public decimal Fees { get; init; }
}

/// <summary>
/// A composed round trip — the entry and exit terms one <c>Trade</c> journals (gh#731, gh#759).
/// </summary>
/// <remarks>
/// A round trip is <b>one opening fill</b> and the closing fills that retire it (ADR-0022): entry terms are that
/// single opening fill's, exit terms are the size-weighted blend of its retiring allocations.
/// </remarks>
/// <param name="OpeningFillId">The opening fill that established this leg — half of the natural key (gh#759).</param>
/// <param name="ClosingFillId">The <b>final</b> fill that retired this leg — the other half of the key.</param>
/// <param name="EntrySide">The side the position <b>entered</b>.</param>
/// <param name="EntryPrice">The opening fill's price (a single fill — exact, not blended).</param>
/// <param name="ExitPrice">The size-weighted average price of the retiring allocations.</param>
/// <param name="Size">The number of contracts this leg round-tripped.</param>
/// <param name="ClosedAt">When the fill that retired the last of this leg happened.</param>
/// <param name="Fees">The opening fill's fees plus the size-shares of the retiring fills' fees.</param>
public sealed record RoundTrip(
    Guid OpeningFillId,
    Guid ClosingFillId,
    OrderSide EntrySide,
    decimal EntryPrice,
    decimal ExitPrice,
    int Size,
    DateTimeOffset ClosedAt,
    decimal Fees);

/// <summary>
/// Composes a contract's executions into the per-leg round trips the journal records (gh#731, gh#759, ADR-0022,
/// R-8/R-9).
/// </summary>
/// <remarks>
/// <para>
/// <b>FIFO lot matching.</b> Walking the fills in time order, a fill that extends exposure (from flat, or on the
/// side the position already holds) <b>opens a leg</b>; a fill on the opposite side <b>retires the oldest open leg
/// first</b>, and a single closing fill that spans two legs is <b>split</b> into per-leg allocations. A leg becomes
/// one <see cref="RoundTrip"/> when it is fully retired — so a scale-in, a partial exit, intermediate flats, and a
/// stop-and-reverse all fall out of the same walk without special-casing (a reversing fill is both the closing fill
/// of the legs it retires and the opening fill of the excess). This <b>supersedes</b> gh#731's single-balanced-trip
/// scope, whose refusal of these shapes left their realized P&amp;L out of the daily governor.
/// </para>
/// <para>
/// <b>Refuse-don't-guess survives for genuine ambiguity.</b> A fill whose side is neither buy nor sell cannot be
/// classified; an <b>open-from-flat whose direction is not decidable</b> — opposite-side fills sharing the exact
/// instant, with no venue sequence here to order them — would let the collection order flip the <em>sign</em> of a
/// trip's P&amp;L; and a window that does not return to flat is not a set of completed trips. Each writes no row.
/// A leg's entry is its opening fill's own price (exact); the exit is a size-weighted blend, so a non-terminating
/// average rounds to a sub-cent amount — far below a tick, and the per-leg realized still sums to the window's total.
/// </para>
/// </remarks>
public static class TradeRoundTrip
{
    /// <summary>Composes the per-leg round trips in <paramref name="fills"/>, if they form completed trips.</summary>
    /// <param name="fills">The contract's executions for one flat-to-flat window, in any order.</param>
    /// <param name="trips">The composed trips, oldest opening leg first; empty when the fills do not form any.</param>
    /// <returns><see langword="true"/> when at least one completed round trip was composed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fills"/> is <see langword="null"/>.</exception>
    public static bool TryCompose(IReadOnlyCollection<RoundTripFill> fills, out IReadOnlyList<RoundTrip> trips)
    {
        ArgumentNullException.ThrowIfNull(fills);

        trips = [];
        if (fills.Count == 0)
        {
            return false;
        }

        // Both legs must be a KNOWN side. An undefined OrderSide -- a bad cast or deserialize, and Order.Side carries
        // no known-value DB check -- cannot be classified as an open or a close, so refuse the whole window rather
        // than let a corrupt side compose a trip whose sign RealizedPnL would trust (gh#734 review).
        if (fills.Any(fill => fill.Side is not (OrderSide.Buy or OrderSide.Sell)))
        {
            return false;
        }

        // Same-instant OPPOSITE-side ambiguity (gh#759 review). When a buy and a sell share the exact execution
        // instant there is no venue sequence here to order them, and their order can flip whether a fill opens,
        // closes, or REVERSES -- flipping a leg's side and the sign of its P&L. The FillId tie-break makes the pick
        // reproducible but not boundary-correct, so refuse the whole window rather than let it decide a sign
        // (refuse-don't-guess). This is the ambiguity gate -- checked up front over the window, not only at an
        // open-from-flat, so a mid-cycle reversal tie is caught too. It is only HALF the guard: it can refuse only a
        // tie that is IN the window handed to it. The caller (TradeJournalService.CurrentCycleStart) is the other half
        // -- it must not slice a flat-to-flat boundary ACROSS a same-instant tie, or the tie is split out of this
        // window and never reaches this check (gh#809). Same-INSTANT same-SIDE ties are safe (FIFO pairing preserves
        // both sign and total), and distinct-instant fills (the norm) are unaffected.
        if (fills.GroupBy(fill => fill.ExecutedAt).Any(group => group.Select(fill => fill.Side).Distinct().Count() > 1))
        {
            return false;
        }

        // Deterministic order: time, then FillId as a stable tiebreak so same-instant same-side fills pair reproducibly.
        RoundTripFill[] ordered = [.. fills.OrderBy(fill => fill.ExecutedAt).ThenBy(fill => fill.FillId)];

        List<RoundTrip> composed = [];
        LinkedList<OpenLeg> open = []; // FIFO queue of open legs -- oldest at the front
        int net = 0; // signed running exposure; its sign is the side the position currently holds

        for (int index = 0; index < ordered.Length; index++)
        {
            RoundTripFill fill = ordered[index];
            int signed = fill.Side == OrderSide.Buy ? fill.Size : -fill.Size;

            // Opening or scaling in: from flat, or on the side already held. Any same-instant opposite-side
            // ambiguity was already refused up front, so the direction here is decidable.
            if (net == 0 || Math.Sign(net) == Math.Sign(signed))
            {
                open.AddLast(new OpenLeg(fill));
                net += signed;
                continue;
            }

            // Closing: opposite side. Retire the oldest open legs FIFO, splitting this fill across them.
            int toAllocate = fill.Size;
            while (toAllocate > 0 && open.First is { Value: OpenLeg leg })
            {
                int take = Math.Min(leg.Remaining, toAllocate);
                leg.Allocate(fill, take);
                toAllocate -= take;
                if (leg.Remaining == 0)
                {
                    composed.Add(leg.Compose());
                    open.RemoveFirst();
                }
            }

            net += signed;

            // Reversal: the fill closed more than was open, so the remainder OPENS a leg the opposite way. The fill is
            // both a close (above) and an open (here); its price and fees are split by size across the two roles.
            if (toAllocate > 0)
            {
                open.AddLast(new OpenLeg(fill with { Size = toAllocate, Fees = SizeShare(fill.Fees, toAllocate, fill.Size) }));
            }
        }

        // Must reconcile to flat: leftover open exposure means this window is not a set of completed round trips.
        if (open.Count != 0)
        {
            return false;
        }

        if (composed.Count == 0)
        {
            return false;
        }

        trips = composed;
        return true;
    }

    /// <summary>The size-proportional share of <paramref name="whole"/> attributable to <paramref name="part"/> of <paramref name="total"/>.</summary>
    private static decimal SizeShare(decimal whole, int part, int total) => whole * part / total;

    /// <summary>An open leg accumulating the closing allocations that retire it, FIFO (gh#759).</summary>
    private sealed class OpenLeg(RoundTripFill opening)
    {
        private readonly List<(decimal Price, int Size, decimal Fees, DateTimeOffset At, Guid FillId)> _closings = [];

        /// <summary>Contracts of this leg not yet retired.</summary>
        public int Remaining { get; private set; } = opening.Size;

        /// <summary>Allocates <paramref name="size"/> of <paramref name="closing"/> against this leg.</summary>
        public void Allocate(RoundTripFill closing, int size)
        {
            _closings.Add((closing.Price, size, SizeShare(closing.Fees, size, closing.Size), closing.ExecutedAt, closing.FillId));
            Remaining -= size;
        }

        /// <summary>Composes the retired leg into a round trip. Call only when <see cref="Remaining"/> is zero.</summary>
        public RoundTrip Compose()
        {
            int exitSize = _closings.Sum(closing => closing.Size); // equals opening.Size once fully retired
            (_, _, _, DateTimeOffset finalAt, Guid finalFillId) = _closings[^1]; // last allocated = final retiring fill
            return new RoundTrip(
                OpeningFillId: opening.FillId,
                ClosingFillId: finalFillId,
                EntrySide: opening.Side,
                EntryPrice: opening.Price,
                ExitPrice: _closings.Sum(closing => closing.Price * closing.Size) / exitSize,
                Size: opening.Size,
                ClosedAt: finalAt,
                Fees: opening.Fees + _closings.Sum(closing => closing.Fees));
        }
    }
}
