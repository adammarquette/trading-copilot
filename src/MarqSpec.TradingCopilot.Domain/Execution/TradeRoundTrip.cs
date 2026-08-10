using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// One execution feeding a round trip — venue facts only (gh#731). The composition below reads nothing else, so
/// it stays a pure function over fills and can be tested without a database or a venue.
/// </summary>
/// <param name="Side">The side this execution traded.</param>
/// <param name="Price">The execution price.</param>
/// <param name="Size">The executed size in contracts; positive.</param>
/// <param name="ExecutedAt">When the execution happened.</param>
public readonly record struct RoundTripFill(OrderSide Side, decimal Price, int Size, DateTimeOffset ExecutedAt)
{
    /// <summary>Fees and commissions attributed to this execution.</summary>
    public decimal Fees { get; init; }
}

/// <summary>
/// A composed round trip — the entry and exit terms a <c>Trade</c> journals (gh#731).
/// </summary>
/// <param name="EntrySide">The side the position <b>entered</b>.</param>
/// <param name="EntryPrice">The size-weighted average entry price.</param>
/// <param name="ExitPrice">The size-weighted average exit price.</param>
/// <param name="Size">The number of contracts round-tripped.</param>
/// <param name="ClosedAt">When the closing execution happened.</param>
/// <param name="Fees">The total fees across both legs.</param>
public sealed record RoundTrip(
    OrderSide EntrySide,
    decimal EntryPrice,
    decimal ExitPrice,
    int Size,
    DateTimeOffset ClosedAt,
    decimal Fees);

/// <summary>
/// Collapses a contract's executions into the single <b>enter → exit → flat</b> round trip the journal records
/// (gh#731, R-8/R-9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Foundational scope, deliberately.</b> This composes the round trip whose two legs <b>balance</b> — the same
/// number of contracts out as in. Scale-in with a partial exit, and stop-and-reverse (flat → re-open opposite),
/// are <b>refused</b> rather than guessed at: they need a pairing policy (FIFO / average / per-leg) that gh#731
/// defers to a follow-up. A refusal writes no <c>Trade</c>, which is the honest outcome — a wrong
/// <c>RealizedPnL</c> feeds the daily governor and would silently mis-state the operator's headroom.
/// </para>
/// <para>
/// The entry leg is the side of the <b>earliest</b> execution; everything on that side is entry, everything on the
/// other side is exit. Both averages are <b>size-weighted</b> and exact in <see langword="decimal"/>.
/// </para>
/// </remarks>
public static class TradeRoundTrip
{
    /// <summary>Composes the balanced round trip described by <paramref name="fills"/>, if there is one.</summary>
    /// <param name="fills">The contract's executions, in any order.</param>
    /// <param name="roundTrip">The composed round trip, or <see langword="null"/> when the fills do not form one.</param>
    /// <returns><see langword="true"/> when a balanced round trip was composed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fills"/> is <see langword="null"/>.</exception>
    public static bool TryCompose(IReadOnlyCollection<RoundTripFill> fills, out RoundTrip? roundTrip)
    {
        ArgumentNullException.ThrowIfNull(fills);

        roundTrip = null;
        if (fills.Count == 0)
        {
            return false;
        }

        // The entry side is whichever side traded FIRST; the opposite side closes it. Both legs must be a KNOWN
        // side. `!= entrySide` was a blacklist, so an undefined OrderSide -- a bad cast or deserialize, and
        // Order.Side carries no known-value DB check -- was bucketed as an exit and could balance into a journalled
        // trade with a corrupt average that RealizedPnL (which validates only the entry side) would never catch
        // (gh#734 review). Whitelist both legs and refuse anything else before composing.
        OrderSide entrySide = fills.OrderBy(fill => fill.ExecutedAt).First().Side;
        if (entrySide is not (OrderSide.Buy or OrderSide.Sell))
        {
            return false;
        }

        OrderSide exitSide = entrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        if (fills.Any(fill => fill.Side != entrySide && fill.Side != exitSide))
        {
            return false; // a fill whose side is neither leg cannot be classified -- refuse the whole trip
        }

        List<RoundTripFill> entries = [.. fills.Where(fill => fill.Side == entrySide)];
        List<RoundTripFill> exits = [.. fills.Where(fill => fill.Side == exitSide)];
        if (exits.Count == 0)
        {
            return false; // never closed -- the position is still open
        }

        int entrySize = entries.Sum(fill => fill.Size);
        int exitSize = exits.Sum(fill => fill.Size);

        // Unbalanced means this is NOT the simple round trip: a scale-in mid-flight, a partial exit, or a
        // reversal. Refuse -- the pairing policy those need is a documented follow-up, not a guess here.
        if (entrySize != exitSize || entrySize == 0)
        {
            return false;
        }

        // BALANCED TOTALS ARE NOT ENOUGH (gh#734 review). Buy 1, Sell 2, Buy 1 sums to 2-vs-2 and would compose as
        // one size-2 long, but exposure went +1 -> -1 -> 0: a stop-and-reverse, two positions in opposite
        // directions whose blended average entry and exit describe neither. Buy 1, Sell 1, Buy 1, Sell 1 balances
        // just as cleanly and is two separate trades. Both would journal money that was never made.
        //
        // So walk the executions in time order and require exposure to reach flat EXACTLY ONCE, on the final fill.
        // Touching zero earlier means the trip already closed; going past zero means it reversed. Either way this
        // is not the single round trip this composer is contracted to form, and refusing is the safe answer -- the
        // caller journals nothing and logs it.
        int running = 0;
        RoundTripFill[] ordered = [.. fills.OrderBy(fill => fill.ExecutedAt)];
        for (int index = 0; index < ordered.Length; index++)
        {
            running += ordered[index].Side == entrySide ? ordered[index].Size : -ordered[index].Size;

            bool isFinalFill = index == ordered.Length - 1;
            if (running == 0 && !isFinalFill)
            {
                return false; // closed before the end -- a completed trip with more activity after it
            }

            if (running < 0)
            {
                return false; // crossed through flat -- a reversal, not a close
            }
        }

        roundTrip = new RoundTrip(
            entrySide,
            SizeWeightedAverage(entries, entrySize),
            SizeWeightedAverage(exits, exitSize),
            entrySize,
            exits.Max(fill => fill.ExecutedAt),
            fills.Sum(fill => fill.Fees));
        return true;
    }

    private static decimal SizeWeightedAverage(List<RoundTripFill> leg, int totalSize) =>
        leg.Sum(fill => fill.Price * fill.Size) / totalSize;
}
