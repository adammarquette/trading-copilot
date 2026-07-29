using MarqSpec.Client.Finnhub;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Integration.Finnhub;

/// <summary>
/// The Finnhub adapter for the R-17 <see cref="INewsSource"/> seam (gh#439, of gh#383): a data-only news source
/// that translates Finnhub's raw <see cref="FinnhubNewsArticle"/> into the venue-neutral <see cref="NewsItem"/>
/// the gh#358 poller fans in and dedups. It declares and requires <see cref="VenueCapability.News"/>; it holds no
/// account and executes nothing.
/// </summary>
/// <remarks>
/// Maps Finnhub's <b>general market news</b> — the category that needs no symbol and gives the cross-asset
/// context the futures desk wants. The provider's Unix-epoch timestamp becomes a UTC <see cref="NewsItem"/>
/// time, and an article with no URL is dropped: the URL is the cross-source dedup identity (<see cref="NewsDedupKey"/>),
/// so one without it could never merge with a corroborating story from another source.
/// </remarks>
public sealed class FinnhubNewsSource : INewsSource
{
    private const string MarketNewsCategory = "general";

    private readonly IFinnhubNewsClient _client;

    /// <summary>Creates the adapter.</summary>
    /// <param name="client">The Finnhub news client.</param>
    public FinnhubNewsSource(IFinnhubNewsClient client) => _client = client;

    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("finnhub");

    /// <inheritdoc />
    public VenueCapabilities Capabilities => VenueCapabilities.Of(VenueCapability.News);

    /// <inheritdoc />
    public int AdapterLogicVersion => 1;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        Capabilities.Require(VenueCapability.News);

        IReadOnlyList<FinnhubNewsArticle> articles = await _client.GetMarketNewsAsync(MarketNewsCategory, cancellationToken);

        List<NewsItem> items = [];
        foreach (FinnhubNewsArticle article in articles)
        {
            if (string.IsNullOrWhiteSpace(article.Url))
            {
                continue; // no URL, no dedup identity — the gh#358 service would drop it anyway
            }

            DateTimeOffset publishedAt = DateTimeOffset.FromUnixTimeSeconds(article.Datetime);
            if (publishedAt < since)
            {
                continue; // older than the lookback window the poller asked for
            }

            items.Add(new NewsItem(
                article.Url,
                article.Headline ?? string.Empty,
                article.Summary ?? string.Empty,
                publishedAt,
                ParseTickers(article.Related)));
        }

        return items;
    }

    private static IReadOnlyList<string> ParseTickers(string? related)
    {
        if (string.IsNullOrWhiteSpace(related))
        {
            return [];
        }

        return [.. related
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
