namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// One raw news / soft-signal item as a source returns it (R-2), before cross-source dedup and relevance mapping.
/// The reference template for non-market feeds — social, transcripts, and economic events reuse this shape.
/// </summary>
/// <param name="Url">The story's URL — the basis of the cross-source dedup identity (see <see cref="NewsDedupKey"/>).</param>
/// <param name="Title">The headline.</param>
/// <param name="Summary">The item's summary or body text as the provider supplies it.</param>
/// <param name="PublishedAt">When the story was published (UTC).</param>
/// <param name="Tickers">
/// The tickers the provider tagged (for example <c>SPY</c>, <c>AAPL</c>) — raw and un-mapped. Resolving these to
/// traded instruments (SPY → ES) is downstream relevance (gh#359), not ingestion.
/// </param>
public sealed record NewsItem(
    string Url,
    string Title,
    string Summary,
    DateTimeOffset PublishedAt,
    IReadOnlyList<string> Tickers);
