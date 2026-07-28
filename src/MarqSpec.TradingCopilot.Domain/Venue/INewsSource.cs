namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The <b>news</b> / soft-signal slice of the venue abstraction — the whole contract for a data-only news provider
/// (R-2, R-17). Finnhub and Tiingo each supply news as cross-asset context and implement this and nothing more.
/// News is deliberately <i>multi-source</i> (Finnhub augmented by Tiingo), collapsed by the ingestion pipeline's
/// cross-source dedup — whereas price data stays single-source.
/// </summary>
/// <remarks>
/// The raw provider HTTP client stays behind this seam (its own <c>MarqSpec.Client.*</c> submodule, mirroring the
/// ProjectX / Tradovate venues); the core sees only <see cref="NewsItem"/>. Relevance — mapping an item's tickers
/// to traded instruments (SPY → ES) — is a separate downstream concern (gh#359), not this seam's job.
/// </remarks>
public interface INewsSource : IVenue
{
    /// <summary>Fetches news items published at or after <paramref name="since"/> (no ordering guarantee).</summary>
    /// <param name="since">The lower bound (inclusive) on publication time — the poll's lookback horizon.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The provider's news items in the window; empty when it has nothing new.</returns>
    /// <exception cref="VenueCapabilityNotSupportedException">
    /// The source does not grant <see cref="VenueCapability.News"/>.
    /// </exception>
    Task<IReadOnlyList<NewsItem>> GetNewsAsync(DateTimeOffset since, CancellationToken cancellationToken = default);
}
