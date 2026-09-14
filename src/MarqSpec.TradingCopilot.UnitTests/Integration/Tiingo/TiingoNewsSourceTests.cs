using FakeItEasy;
using MarqSpec.Client.Tiingo;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Tiingo;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tiingo;

/// <summary>
/// The Tiingo → <see cref="INewsSource"/> adapter (gh#440). It translates Tiingo's raw article shape — ISO-8601
/// dates and array tickers, unlike Finnhub's epoch + CSV — into the venue-neutral <see cref="NewsItem"/> the
/// gh#358 poller consumes. The mapping is the whole job, guarded here against a faked client (no network).
/// </summary>
public class TiingoNewsSourceTests
{
    private static DateTimeOffset Epoch => DateTimeOffset.UnixEpoch;

    private readonly ITiingoNewsClient _client = A.Fake<ITiingoNewsClient>();

    private TiingoNewsSource Source() => new(_client);

    private void ClientReturns(params TiingoNewsArticle[] articles) =>
        A.CallTo(() => _client.GetNewsAsync(A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<TiingoNewsArticle>>(articles));

    private static TiingoNewsArticle Article(
        string? url = "https://ex.com/a",
        DateTimeOffset? publishedDate = null,
        string? title = "Fed holds rates",
        string? description = "Rates unchanged.",
        IReadOnlyList<string>? tickers = null) =>
        new(1, title, description, url, publishedDate ?? new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
            CrawlDate: default, "reuters.com", tickers ?? ["SPY", "QQQ"], ["Fed"]);

    [Fact]
    public void Id_ShouldBeTiingo_SoProvenanceTagsTheRightFeed()
    {
        Source().Id.ToString().Should().Be("tiingo");
    }

    [Fact]
    public void Capabilities_ShouldDeclareNews()
    {
        Source().Capabilities.Supports(VenueCapability.News).Should().BeTrue();
    }

    [Fact]
    public async Task GetNewsAsync_ShouldMapATiingoArticleToANewsItem()
    {
        DateTimeOffset published = new(2026, 7, 29, 13, 30, 0, TimeSpan.Zero);
        ClientReturns(Article(
            url: "https://ex.com/story",
            publishedDate: published,
            title: "Crude inventories drop",
            description: "Larger-than-expected draw.",
            tickers: ["CL", "RB"]));

        NewsItem item = (await Source().GetNewsAsync(Epoch, CancellationToken.None)).Should().ContainSingle().Subject;

        item.Url.Should().Be("https://ex.com/story");
        item.Title.Should().Be("Crude inventories drop");
        item.Summary.Should().Be("Larger-than-expected draw.");
        item.PublishedAt.Should().Be(published);
        item.Tickers.Should().BeEquivalentTo(["CL", "RB"]);
    }

    [Fact]
    public async Task GetNewsAsync_ShouldDropArticlesOlderThanTheLookback()
    {
        DateTimeOffset since = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        ClientReturns(
            Article(url: "https://ex.com/old", publishedDate: since.AddMinutes(-1)),
            Article(url: "https://ex.com/new", publishedDate: since.AddMinutes(1)));

        IReadOnlyList<NewsItem> items = await Source().GetNewsAsync(since, CancellationToken.None);

        items.Should().ContainSingle().Which.Url.Should().Be("https://ex.com/new");
    }

    [Fact]
    public async Task GetNewsAsync_ShouldDropAnArticleWithNoUrl()
    {
        ClientReturns(
            Article(url: null, title: "No URL"),
            Article(url: "https://ex.com/has-url", title: "Has URL"));

        (await Source().GetNewsAsync(Epoch, CancellationToken.None)).Should().ContainSingle()
            .Which.Title.Should().Be("Has URL");
    }

    [Fact]
    public async Task GetNewsAsync_ShouldYieldNoTickers_WhenTiingoReturnsNullTickers()
    {
        // Tiingo can omit the tickers array; that is "no tickers", not a crash. Built inline with a genuine null
        // rather than via the Article() helper, whose `?? [defaults]` would substitute a non-null list and make
        // this assertion vacuous.
        ClientReturns(new TiingoNewsArticle(
            1, "Fed holds rates", "Rates unchanged.", "https://ex.com/a",
            new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero), CrawlDate: default, "reuters.com",
            Tickers: null, Tags: null));

        (await Source().GetNewsAsync(Epoch, CancellationToken.None)).Should().ContainSingle()
            .Which.Tickers.Should().BeEmpty();
    }

    [Fact]
    public async Task GetNewsAsync_ShouldTolerateNullTitleAndDescription()
    {
        ClientReturns(Article(title: null, description: null));

        NewsItem item = (await Source().GetNewsAsync(Epoch, CancellationToken.None)).Should().ContainSingle().Subject;
        item.Title.Should().BeEmpty();
        item.Summary.Should().BeEmpty();
    }
}
