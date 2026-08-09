using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain;

/// <summary>
/// The contract details a price needs to become money: tick size and point value. Sizing and every risk layer
/// run through <see cref="LossPerContract"/>, so the money math lives in exactly one place.
/// </summary>
public sealed record InstrumentSpec
{
    private InstrumentSpec(InstrumentId id, decimal tickSize, decimal pointValue)
    {
        Id = id;
        TickSize = tickSize;
        PointValue = pointValue;
    }

    /// <summary>The instrument this spec describes.</summary>
    public InstrumentId Id { get; }

    /// <summary>The smallest price increment (ES: <c>0.25</c>).</summary>
    public decimal TickSize { get; }

    /// <summary>The dollar value of a one-point move in one contract (ES: <c>50</c>).</summary>
    public decimal PointValue { get; }

    /// <summary>Creates a spec.</summary>
    /// <param name="id">The instrument.</param>
    /// <param name="tickSize">The smallest price increment; must be positive.</param>
    /// <param name="pointValue">The dollar value of one point; must be positive.</param>
    /// <returns>The instrument spec.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tickSize"/> or <paramref name="pointValue"/> is not positive.</exception>
    public static InstrumentSpec Create(InstrumentId id, decimal tickSize, decimal pointValue)
    {
        if (tickSize <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "Tick size must be positive.");
        }

        if (pointValue <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(pointValue), pointValue, "Point value must be positive.");
        }

        return new InstrumentSpec(id, tickSize, pointValue);
    }

    /// <summary>The money one contract loses moving from <paramref name="entry"/> to <paramref name="exit"/>.</summary>
    /// <param name="entry">The entry price.</param>
    /// <param name="exit">The exit price (a stop).</param>
    /// <returns>The absolute loss per contract — direction-agnostic, so longs and shorts size identically.</returns>
    public decimal LossPerContract(Price entry, Price exit)
    {
        return Math.Abs(entry.Value - exit.Value) * PointValue;
    }

    /// <summary>
    /// The <b>signed</b> realized money a round trip of <paramref name="size"/> contract(s) made or lost, having
    /// entered <paramref name="side"/> at <paramref name="entry"/> and exited at <paramref name="exit"/>. Positive
    /// is a profit, negative a loss. Unlike <see cref="LossPerContract"/> (absolute, for direction-agnostic sizing),
    /// this is <b>direction-aware</b> — a long profits when the exit is above the entry, a short when it is below —
    /// so it is the realized P&amp;L the journal records (gh#731). Exact <see langword="decimal"/>: money is never
    /// rounded here.
    /// </summary>
    /// <param name="entry">The average entry price.</param>
    /// <param name="exit">The average exit price.</param>
    /// <param name="side">The side the position <b>entered</b> — <see cref="OrderSide.Buy"/> is long, <see cref="OrderSide.Sell"/> is short.</param>
    /// <param name="size">The number of contracts closed; must be positive.</param>
    /// <returns>The signed realized P&amp;L in account currency.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not positive.</exception>
    public decimal RealizedPnL(Price entry, Price exit, OrderSide side, int size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Size must be positive.");
        }

        // WHITELIST, not `Buy ? 1 : -1` (gh#734 review). That blacklist gave every non-Buy value the short sign,
        // including a value that is not a side at all -- and `Order.Side` carries no known-value database CHECK, so
        // a cast or a deserialization can produce one. The result was INVERTED realized P&L: a winning long
        // journalled as a loss, feeding DailyRealizedReader and the R-5 governor. An unknown side has no direction,
        // so it fails closed here rather than picking one (ADR-0007's whitelist rule).
        int sideSign = side switch
        {
            OrderSide.Buy => 1,
            OrderSide.Sell => -1,
            _ => throw new ArgumentOutOfRangeException(
                nameof(side), side, "Realized P&L cannot be signed by an unrecognised order side."),
        };

        return (exit.Value - entry.Value) * sideSign * size * PointValue;
    }

    /// <summary>The notional value one contract represents at <paramref name="price"/>.</summary>
    /// <param name="price">The price to value the contract at.</param>
    /// <returns>Price times point value.</returns>
    public decimal NotionalPerContract(Price price)
    {
        return price.Value * PointValue;
    }

    /// <summary>The distance between two prices expressed in ticks.</summary>
    /// <param name="from">The first price.</param>
    /// <param name="to">The second price.</param>
    /// <returns>The absolute distance in ticks.</returns>
    public decimal TicksBetween(Price from, Price to)
    {
        return Math.Abs(from.Value - to.Value) / TickSize;
    }
}
