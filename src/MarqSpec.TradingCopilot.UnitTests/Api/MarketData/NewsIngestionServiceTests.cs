using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// News ingestion (gh#358, R-2) — the store of record, deduped across sources. Two quiet failures carry the weight:
/// a <b>duplicate</b> (the same story stored twice double-counts everywhere downstream), and a <b>lost source</b>
/// (a provider that throws must not cost the others their pass). Relevance mapping is downstream (gh#359).
/// </summary>
public class NewsIngestionServiceTests
{
    private static DateTimeOffset Now => new(2026, 7, 20, 15, 0, 0, TimeSpan.Zero);

    private static VenueId Finnhub => VenueId.Parse("finnhub");

    private static VenueId Tiingo => VenueId.Parse("tiingo");

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    private static NewsItem Item(string url, string title = "Fed holds rates", params string[] tickers) =>
        new(url, title, "The committee left rates unchanged.", Now.AddMinutes(-5), tickers);

    private static INewsSource Source(VenueId id, IReadOnlyList<NewsItem> items, Exception? throws = null)
    {
        INewsSource source = A.Fake<INewsSource>();
        A.CallTo(() => source.Id).Returns(id);

        if (throws is not null)
        {
            A.CallTo(() => source.GetNewsAsync(A<DateTimeOffset>._, A<CancellationToken>._)).Throws(throws);
        }
        else
        {
            A.CallTo(() => source.GetNewsAsync(A<DateTimeOffset>._, A<CancellationToken>._)).Returns(items);
        }

        return source;
    }

    private NewsIngestionService Service(TradingCopilotDbContext context, params INewsSource[] sources) =>
        new(context, sources, Options.Create(new NewsIngestionOptions { Enabled = true }),
            NullLogger<NewsIngestionService>.Instance);

    // --- It stores what it fetched ---

    [Fact]
    public async Task IngestAsync_ShouldStoreFetchedNews()
    {
        await using TradingCopilotDbContext context = Context();
        INewsSource source = Source(Finnhub, [Item("https://site.com/a"), Item("https://site.com/b")]);

        await Service(context, source).IngestAsync(Now, CancellationToken.None);

        (await context.News.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task IngestAsync_ShouldRecordTheItemFieldsAndProvenance()
    {
        await using TradingCopilotDbContext context = Context();
        INewsSource source = Source(Finnhub, [Item("https://site.com/fomc", "Powell speaks", "SPY", "QQQ")]);

        await Service(context, source).IngestAsync(Now, CancellationToken.None);

        NewsRecord stored = await context.News.SingleAsync();
        stored.Title.Should().Be("Powell speaks");
        stored.Type.Should().Be("news");
        stored.Url.Should().Be("https://site.com/fomc");
        stored.Tickers.Should().BeEquivalentTo(["SPY", "QQQ"]);
        stored.SourceFeeds.Should().ContainSingle().Which.Should().Be("finnhub");
    }

    // --- Cross-source dedup: the headline behaviour ---

    [Fact]
    public async Task IngestAsync_ShouldCollapseToOneRecordWithBothProvenances_WhenTwoSourcesCarryTheSameStory()
    {
        // News is deliberately multi-source. The same story from Finnhub AND Tiingo must be one row whose
        // provenance records both -- storing it twice would double-count it in every downstream salience.
        await using TradingCopilotDbContext context = Context();
        const string url = "https://site.com/shared-story";
        INewsSource finnhub = Source(Finnhub, [Item(url)]);
        INewsSource tiingo = Source(Tiingo, [Item(url)]);

        await Service(context, finnhub, tiingo).IngestAsync(Now, CancellationToken.None);

        NewsRecord stored = await context.News.SingleAsync();
        stored.SourceFeeds.Should().BeEquivalentTo(["finnhub", "tiingo"]);
    }

    [Fact]
    public async Task IngestAsync_ShouldCollapseTrackingVariants_IntoOneRecord()
    {
        // The two feeds decorate the same article differently; the canonical dedup key collapses them.
        await using TradingCopilotDbContext context = Context();
        INewsSource finnhub = Source(Finnhub, [Item("https://www.site.com/story?utm_source=finnhub")]);
        INewsSource tiingo = Source(Tiingo, [Item("https://site.com/story/")]);

        await Service(context, finnhub, tiingo).IngestAsync(Now, CancellationToken.None);

        (await context.News.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IngestAsync_ShouldKeepDistinctStoriesDistinct()
    {
        await using TradingCopilotDbContext context = Context();
        INewsSource source = Source(Finnhub, [Item("https://site.com/a"), Item("https://site.com/b")]);

        await Service(context, source).IngestAsync(Now, CancellationToken.None);

        (await context.News.CountAsync()).Should().Be(2);
    }

    // --- Idempotence: the normal case, not an edge one ---

    [Fact]
    public async Task IngestAsync_ShouldNotDuplicate_WhenTheSameWindowIsPolledTwice()
    {
        // Overlapping polls are how a periodic ingest WORKS -- each pass re-fetches a lookback window.
        await using TradingCopilotDbContext context = Context();
        INewsSource source = Source(Finnhub, [Item("https://site.com/a"), Item("https://site.com/b")]);
        NewsIngestionService service = Service(context, source);

        await service.IngestAsync(Now, CancellationToken.None);
        await service.IngestAsync(Now, CancellationToken.None);

        (await context.News.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task IngestAsync_ShouldUnionProvenance_WhenASecondSourceCarriesItLater()
    {
        // First only Finnhub has the story; a later pass sees Tiingo carry it too. No new row -- provenance grows.
        await using TradingCopilotDbContext context = Context();
        const string url = "https://site.com/shared";
        NewsIngestionService first = Service(context, Source(Finnhub, [Item(url)]));
        await first.IngestAsync(Now, CancellationToken.None);

        NewsIngestionService second = Service(context, Source(Finnhub, [Item(url)]), Source(Tiingo, [Item(url)]));
        await second.IngestAsync(Now.AddMinutes(1), CancellationToken.None);

        (await context.News.CountAsync()).Should().Be(1);
        (await context.News.SingleAsync()).SourceFeeds.Should().BeEquivalentTo(["finnhub", "tiingo"]);
    }

    // --- Failure containment ---

    [Fact]
    public async Task IngestAsync_ShouldNotThrowAndKeepOtherSources_WhenOneRefusesTheCapability()
    {
        // R-17: a source without News refuses at the seam -- a configuration truth, not a crash. The other feed
        // still lands.
        await using TradingCopilotDbContext context = Context();
        INewsSource refuses = Source(Finnhub, [], new VenueCapabilityNotSupportedException(VenueCapability.News));
        INewsSource works = Source(Tiingo, [Item("https://site.com/a")]);

        Func<Task> act = () => Service(context, refuses, works).IngestAsync(Now, CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await context.News.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IngestAsync_ShouldContinueToTheNextSource_WhenOneThrows()
    {
        // One bad provider must not cost the rest their pass.
        await using TradingCopilotDbContext context = Context();
        INewsSource broken = Source(Finnhub, [], new InvalidOperationException("provider hiccup"));
        INewsSource works = Source(Tiingo, [Item("https://site.com/a")]);

        await Service(context, broken, works).IngestAsync(Now, CancellationToken.None);

        (await context.News.SingleAsync()).SourceFeeds.Should().ContainSingle().Which.Should().Be("tiingo");
    }

    [Fact]
    public async Task IngestAsync_ShouldDropItemsWithNoUrl()
    {
        // No URL, no dedup identity -- an unmergeable row is worse than a dropped one.
        await using TradingCopilotDbContext context = Context();
        INewsSource source = Source(Finnhub, [Item(""), Item("https://site.com/a")]);

        await Service(context, source).IngestAsync(Now, CancellationToken.None);

        (await context.News.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IngestAsync_ShouldDoNothing_WhenNoSourcesAreRegistered()
    {
        // Until a provider adapter (its own MarqSpec.Client.* submodule) is wired, an enabled poller has no feeds.
        await using TradingCopilotDbContext context = Context();

        int written = await Service(context).IngestAsync(Now, CancellationToken.None);

        written.Should().Be(0);
        (await context.News.CountAsync()).Should().Be(0);
    }
}
