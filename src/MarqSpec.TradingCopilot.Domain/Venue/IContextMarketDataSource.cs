namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A <b>cross-asset context</b> price source (gh#496, R-1 / R-17) — the whole contract for a provider that
/// streams last-trade prints and nothing else. Finnhub supplies equities/indices context (SPY ↔ ES, QQQ ↔ NQ)
/// with no book, no accounts and no execution, so it implements this and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate seam from <see cref="IMarketDataSource"/>.</b> That one yields <see cref="Quote"/> —
/// top-of-book prices the execution path may act on. A free-tier context provider has no book to publish, so
/// forcing it through that seam would mean inventing a bid and an ask. The split keeps the difference in the type
/// system rather than in a comment: a consumer that needs executable prices takes <see cref="IMarketDataSource"/>
/// and cannot be handed a context source, and the two feeds reach the log under <b>different event types</b>, so
/// the execution watchers cannot read context data even by accident (operator decision, 2026-08-02).
/// </para>
/// <para>
/// Transport stays behind the seam, as everywhere in R-17: Finnhub streams trades over one websocket with a
/// free-tier subscription cap, and none of that shape may leak into the core.
/// </para>
/// </remarks>
public interface IContextMarketDataSource : IVenue
{
    /// <summary>Resolves a venue-neutral instrument to this source's own contract handle.</summary>
    /// <param name="instrument">The instrument to resolve (for example <c>SPY</c>).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The source's contract handle paired with the instrument it was resolved for — the pairing that keeps a
    /// context symbol and a tradeable contract from ever being confused for one another.
    /// </returns>
    Task<ResolvedContract> ResolveContractAsync(InstrumentId instrument, CancellationToken cancellationToken = default);

    /// <summary>Streams last-trade prints until the caller stops enumerating or cancels (R-1).</summary>
    /// <param name="contract">The contract to subscribe to.</param>
    /// <param name="cancellationToken">Ends the subscription.</param>
    /// <returns>An asynchronous sequence of context trades.</returns>
    /// <exception cref="VenueCapabilityNotSupportedException">
    /// The source does not grant <see cref="VenueCapability.ContextTrades"/>.
    /// </exception>
    IAsyncEnumerable<ContextTrade> StreamContextTradesAsync(
        VenueContractId contract,
        CancellationToken cancellationToken = default);
}
