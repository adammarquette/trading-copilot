namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The <b>market-data</b> slice of the venue abstraction — and the <i>whole</i> contract for a data-only provider
/// (R-17). Finnhub supplies equities/indices context (SPY ↔ ES, QQQ ↔ NQ) with no accounts and no execution, so
/// it implements this and nothing more.
/// </summary>
/// <remarks>
/// Transport stays behind this seam: ProjectX streams from one realtime host with two hubs, Tradovate from two
/// separate sockets, and neither shape may leak into the core.
/// </remarks>
public interface IMarketDataSource : IVenue
{
    /// <summary>Resolves a venue-neutral instrument to this venue's own contract handle.</summary>
    /// <param name="instrument">The instrument to resolve (for example <c>ES</c>).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The venue's contract handle for the instrument.</returns>
    Task<VenueContractId> ResolveContractAsync(InstrumentId instrument, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves historical bars — the clean series that is the system of record for journaling and replay (R-1).
    /// </summary>
    /// <param name="contract">The contract to retrieve bars for.</param>
    /// <param name="from">Start of the range, inclusive.</param>
    /// <param name="to">End of the range, exclusive.</param>
    /// <param name="barSize">The bar duration.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The bars in ascending time order.</returns>
    /// <exception cref="VenueCapabilityNotSupportedException">
    /// The venue does not grant <see cref="VenueCapability.HistoricalBars"/>.
    /// </exception>
    Task<IReadOnlyList<Bar>> GetBarsAsync(
        VenueContractId contract,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan barSize,
        CancellationToken cancellationToken = default);

    /// <summary>Streams live quotes until the caller stops enumerating or cancels (R-1).</summary>
    /// <param name="contract">The contract to subscribe to.</param>
    /// <param name="cancellationToken">Ends the subscription.</param>
    /// <returns>An asynchronous sequence of quotes.</returns>
    /// <exception cref="VenueCapabilityNotSupportedException">
    /// The venue does not grant <see cref="VenueCapability.Quotes"/>.
    /// </exception>
    IAsyncEnumerable<Quote> StreamQuotesAsync(VenueContractId contract, CancellationToken cancellationToken = default);
}
