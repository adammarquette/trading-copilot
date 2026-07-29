using FakeItEasy;
using MarqSpec.Client.Finnhub;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Finnhub;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Finnhub;

/// <summary>
/// The Finnhub → <see cref="INewsSource"/> adapter (gh#439). It translates Finnhub's raw article shape into the
/// venue-neutral <see cref="NewsItem"/> the gh#358 poller consumes — the mapping is the whole job, so that is
/// what these guard, against a faked client (no network).
/// </summary>
public class FinnhubNewsSourceTests
{
    private static DateTimeOffset Epoch => DateTimeOffset.UnixEpoch;

    private readonly IFinnhubNewsClient _client = A.Fake<IFinnhubNewsClient>();

    private FinnhubNewsSource Source() => new(_client);

    private void ClientReturns(params FinnhubNewsArticle[] articles) =>
        A.CallTo(() => _client.GetMarketNewsAsync(A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<FinnhubNewsArticle>>(articles));

    private static FinnhubNewsArticle Article(
        string? url = "https://ex.com/a",
        long datetime = 1_700_000_000,
        string? headline = "Fed holds rates",
        string? summary = "Rates unchanged.",
        string? related = "SPY,QQQ") =>
        new("general", datetime, headline, 1, null, related, "Reuters", summary, url);

    [Fact]
    public void Id_ShouldBeFinnhub_SoProvenanceTagsTheRightFeed()
    {
        // #358 records provenance by source.Id.ToString(); it must render "finnhub".
        Source().Id.ToString().Should().Be("finnhub");
    }

    [Fact]
    public void Capabilities_ShouldDeclareNews()
    {
        Source().Capabilities.Supports(VenueCapability.News).Should().BeTrue();
    }

    [Fact]
    public async Task GetNewsAsync_ShouldMapAFinnhubArticleToANewsItem()
    {
        ClientReturns(Article(
            url: "https://ex.com/story",
            datetime: 1_700_000_000,
            headline: "Crude inventories drop",
            summary: "Larger-than-expected draw.",
            related: "CL,RB"));

        NewsItem item = (await Source().GetNewsAsync(Epoch, CancellationToken.None)).Should().ContainSingle().Subject;

        item.Url.Should().Be("https://ex.com/story");
        item.Title.Should().Be("Crude inventories drop");
        item.Summary.Should().Be("Larger-than-expected draw.");
        item.PublishedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        item.Tickers.Should().BeEquivalentTo(["CL", "RB"]);
    }

    [Fact]
    public async Task GetNewsAsync_ShouldDropArticlesOlderThanTheLookback()
    {
        DateTimeOffset since = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        ClientReturns(
            Article(url: "https://ex.com/old", datetime: 1_699_999_000),  // before since
            Article(url: "https://ex.com/new", datetime: 1_700_000_500));  // after since

        IReadOnlyList<NewsItem> items = await Source().GetNewsAsync(since, CancellationToken.None);

        items.Should().ContainSingle().Which.Url.Should().Be("https://ex.com/new");
    }

    [Fact]
    public async Task GetNewsAsync_ShouldDropAnArticleWithNoUrl()
    {
        // The URL is the cross-source dedup identity (NewsDedupKey); one without it could never merge.
        ClientReturns(
            Article(url: null, headline: "No URL"),
            Article(url: "https://ex.com/has-url", headline: "Has URL"));

        IReadOnlyList<NewsItem> items = await Source().GetNewsAsync(Epoch, CancellationToken.None);

        items.Should().ContainSingle().Which.Title.Should().Be("Has URL");
    }

    [Fact]
    public async Task GetNewsAsync_ShouldYieldNoTickers_WhenRelatedIsEmpty()
    {
        ClientReturns(Article(related: ""));

        (await Source().GetNewsAsync(Epoch, CancellationToken.None)).Should().ContainSingle()
            .Which.Tickers.Should().BeEmpty();
    }

    [Fact]
    public async Task GetNewsAsync_ShouldTrimAndSplitRelatedTickers()
    {
        ClientReturns(Article(related: "SPY, QQQ ,,IWM"));

        (await Source().GetNewsAsync(Epoch, CancellationToken.None)).Should().ContainSingle()
            .Which.Tickers.Should().BeEquivalentTo(["SPY", "QQQ", "IWM"]);
    }

    [Fact]
    public async Task GetNewsAsync_ShouldTolerateNullHeadlineAndSummary()
    {
        ClientReturns(Article(headline: null, summary: null));

        NewsItem item = (await Source().GetNewsAsync(Epoch, CancellationToken.None)).Should().ContainSingle().Subject;
        item.Title.Should().BeEmpty();
        item.Summary.Should().BeEmpty();
    }
}
