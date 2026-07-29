using MarqSpec.Client.Tiingo;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Integration.Tiingo;

/// <summary>
/// The Tiingo adapter for the R-17 <see cref="INewsSource"/> seam (gh#440, of gh#383): the second data-only news
/// source, running alongside Finnhub (gh#439). It translates Tiingo's raw <see cref="TiingoNewsArticle"/> into
/// the venue-neutral <see cref="NewsItem"/> the gh#358 poller fans in and dedups — the same story from both
/// sources collapsing to one <c>NewsRecord</c>. It declares and requires <see cref="VenueCapability.News"/>;
/// Tiingo's price surface is not wired (price data stays single-source, Finnhub).
/// </summary>
/// <remarks>
/// Two shape differences from the Finnhub adapter, handled here: Tiingo dates are ISO-8601 (already a
/// <see cref="DateTimeOffset"/>, no epoch conversion), and tickers arrive as a JSON array (no comma-splitting).
/// An article with no URL is dropped — the URL is the cross-source dedup identity (<see cref="NewsDedupKey"/>).
/// Tiingo's <c>startDate</c> filter is date-granular, so the exact-instant <c>since</c> re-filter happens here.
/// </remarks>
public sealed class TiingoNewsSource : INewsSource
{
    private readonly ITiingoNewsClient _client;

    /// <summary>Creates the adapter.</summary>
    /// <param name="client">The Tiingo news client.</param>
    public TiingoNewsSource(ITiingoNewsClient client) => _client = client;

    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("tiingo");

    /// <inheritdoc />
    public VenueCapabilities Capabilities => VenueCapabilities.Of(VenueCapability.News);

    /// <inheritdoc />
    public int AdapterLogicVersion => 1;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        Capabilities.Require(VenueCapability.News);

        IReadOnlyList<TiingoNewsArticle> articles = await _client.GetNewsAsync(since, cancellationToken);

        List<NewsItem> items = [];
        foreach (TiingoNewsArticle article in articles)
        {
            if (string.IsNullOrWhiteSpace(article.Url))
            {
                continue; // no URL, no dedup identity — the gh#358 service would drop it anyway
            }

            if (article.PublishedDate < since)
            {
                continue; // the date-granular startDate can return same-day items older than the exact lookback
            }

            items.Add(new NewsItem(
                article.Url,
                article.Title ?? string.Empty,
                article.Description ?? string.Empty,
                article.PublishedDate,
                article.Tickers ?? []));
        }

        return items;
    }
}
