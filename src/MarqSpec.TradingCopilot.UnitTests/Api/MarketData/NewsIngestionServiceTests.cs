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
        Service(context, new NewsIngestionOptions { Enabled = true }, sources);

    private NewsIngestionService Service(
        TradingCopilotDbContext context, NewsIngestionOptions options, params INewsSource[] sources) =>
        new(context, sources, Options.Create(options), NullLogger<NewsIngestionService>.Instance);

    private static NewsRecord Stored(string url, string title, DateTimeOffset published) =>
        new()
        {
            DedupKey = NewsDedupKey.For(url),
            Type = "news",
            Url = url,
            Title = title,
            Summary = "Stored.",
            PublishedAt = published,
            Tickers = ["ES"],
            SourceFeeds = ["finnhub"],
            RecordedAt = Now,
        };

    [Fact]
    public async Task IngestAsync_ShouldNotLoadFuzzyCandidates_WhenEveryPendingAlreadyExists()
    {
        // gh#836. The cross-pass candidate query is consulted ONLY by FindFuzzyStored, which sits in the `else`
        // after the canonical lookup fails — so on a typical pass, where every pending is already stored, it
        // materialized a window of rows as TRACKED entities that nothing ever read.
        //
        // Asserted through the change tracker rather than a query spy, because that is what "the query ran" leaves
        // behind: a row inside the window but outside this pass's keys is tracked if and only if it was fetched.
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.News.Add(Stored("https://site.com/a", "Fed holds rates", Now.AddMinutes(-5)));
            seed.News.Add(Stored("https://site.com/unrelated", "Jobless claims fall", Now.AddMinutes(-10)));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        await Service(context, Source(Finnhub, [Item("https://site.com/a")]))
            .IngestAsync(Now, CancellationToken.None);

        context.ChangeTracker.Entries<NewsRecord>().Select(entry => entry.Entity.Url)
            .Should().NotContain(
                "https://site.com/unrelated",
                "a pass whose pendings all hit the canonical lookup never consults a fuzzy candidate, so it must not fetch one");
    }

    [Fact]
    public async Task IngestAsync_ShouldWidenTheCandidateWindowWithTheConfiguredGap_NotTheDefaultConstant()
    {
        // gh#836's trap: the configured gap governs BOTH the pairwise comparison and the window this query loads.
        // Size the window off the constant while the comparison honours a wider configured value and the knob is
        // silently inert past 60 minutes — the pair would be admissible but never fetched to be compared.
        await using (TradingCopilotDbContext seed = Context())
        {
            // Stored 90 minutes before the arriving item: outside the default window, inside a configured 180.
            seed.News.Add(Stored("https://other.com/same-story", "Fed holds rates", Now.AddMinutes(-95)));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        NewsIngestionOptions options = new() { Enabled = true, FuzzyMaxPublishedGapMinutes = 180 };

        // A different URL — so canonical dedup misses and the fuzzy path is the only thing that can merge it.
        await Service(context, options, Source(Tiingo, [Item("https://site.com/a", "Fed holds rates", "ES")]))
            .IngestAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext read = Context();
        List<NewsRecord> rows = await read.News.ToListAsync(CancellationToken.None);
        rows.Should().HaveCount(1, "the stored story was inside the CONFIGURED window, so it merged rather than duplicating");
        rows[0].SourceFeeds.Should().Contain("tiingo");
    }

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

    // --- Fuzzy fallback (R-2, gh#764): same story, DIFFERENT canonical URLs ---

    [Fact]
    public async Task IngestAsync_ShouldCollapseSameStoryUnderDifferentUrls_ViaTheFuzzyFallback()
    {
        // The headline gh#764 behaviour: Finnhub and Tiingo carry ONE story under different (non-canonical-matching)
        // URLs. Canonical dedup gives two keys, so without the fuzzy fallback this is two rows; the fallback -- near-
        // duplicate title + same publish time + shared ticker -- collapses them to one whose provenance records both.
        await using TradingCopilotDbContext context = Context();
        INewsSource finnhub = Source(
            Finnhub, [Item("https://finnhub.example/apple-iphone-17", "Apple unveils the iPhone 17 at its fall event", "AAPL")]);
        INewsSource tiingo = Source(
            Tiingo, [Item("https://tiingo.example/aapl/iphone", "Apple unveils iPhone 17", "AAPL")]);

        await Service(context, finnhub, tiingo).IngestAsync(Now, CancellationToken.None);

        NewsRecord stored = await context.News.SingleAsync();
        stored.SourceFeeds.Should().BeEquivalentTo(["finnhub", "tiingo"]);
    }

    [Fact]
    public async Task IngestAsync_ShouldNotCollapseDistinctStoriesOnTheSameTicker_ViaTheFuzzyFallback()
    {
        // The false-positive direction, which matters more: two genuinely different stories about AAPL minutes apart
        // must stay two rows -- a wrongly-merged row destroys a real signal. Same ticker, close in time, but the
        // titles are not similar, so the conjunction refuses.
        await using TradingCopilotDbContext context = Context();
        INewsSource finnhub = Source(Finnhub, [Item("https://finnhub.example/1", "Apple unveils iPhone 17", "AAPL")]);
        INewsSource tiingo = Source(Tiingo, [Item("https://tiingo.example/2", "Apple stock slides after earnings miss", "AAPL")]);

        await Service(context, finnhub, tiingo).IngestAsync(Now, CancellationToken.None);

        (await context.News.CountAsync()).Should().Be(2, "distinct same-ticker stories must not be fuzzy-merged");
    }

    [Fact]
    public async Task IngestAsync_ShouldStoreBothAsSeparateRows_WhenOneFeedCarriesTwoSimilarHeadlinesUnderDifferentUrls()
    {
        // gh#827 regression. The fuzzy fallback exists for a SECOND feed disagreeing on the canonical URL. Within a
        // SINGLE feed's pass, two near-duplicate headlines under different URLs are distinct items (a follow-up, a
        // correction, a re-headline) -- unioning the feed onto the first is a HashSet no-op that silently drops the
        // second, which the pre-fallback code stored. The fallback must be cross-feed only: a same-feed near-duplicate
        // falls through to its own row. (These are the exact titles the cross-feed fuzzy test collapses, so the only
        // thing under test is that ONE feed does not.)
        await using TradingCopilotDbContext context = Context();
        INewsSource finnhub = Source(
            Finnhub,
            [
                Item("https://finnhub.example/apple-1", "Apple unveils the iPhone 17 at its fall event", "AAPL"),
                Item("https://finnhub.example/apple-2", "Apple unveils iPhone 17", "AAPL"),
            ]);

        await Service(context, finnhub).IngestAsync(Now, CancellationToken.None);

        (await context.News.CountAsync()).Should().Be(
            2, "two near-duplicate headlines from ONE feed are distinct items -- the fuzzy fallback is cross-feed only");
    }

    [Fact]
    public async Task IngestAsync_ShouldUnionTickersFromBothFeeds_WhenTheFuzzyFallbackMergesWithinAPass()
    {
        // gh#827 review. A fuzzy merge is cross-PROVIDER by construction -- each feed tags its own metadata. Unioning
        // provenance but keeping only the first feed's tickers loses signal: gh#359 resolves relevance from Tickers,
        // so a ticker only Tiingo tagged (SPY -> ES) would never map. The merged row must carry both feeds' tickers.
        await using TradingCopilotDbContext context = Context();
        INewsSource finnhub = Source(
            Finnhub, [Item("https://finnhub.example/apple", "Apple unveils the iPhone 17 at its fall event", "AAPL")]);
        INewsSource tiingo = Source(
            Tiingo, [Item("https://tiingo.example/aapl", "Apple unveils iPhone 17", "AAPL", "SPY")]);

        await Service(context, finnhub, tiingo).IngestAsync(Now, CancellationToken.None);

        NewsRecord stored = await context.News.SingleAsync();
        stored.Tickers.Should().BeEquivalentTo(["AAPL", "SPY"], "a fuzzy merge must not discard the other feed's tickers");
    }

    [Fact]
    public async Task IngestAsync_ShouldUnionTickersOntoTheStoredRow_WhenTheFuzzyFallbackMergesAcrossPasses()
    {
        // The cross-pass half of the same loss: pass one stores AAPL only; a later pass's different-URL copy also tags
        // SPY. The stored row must gain SPY, not keep AAPL alone (gh#827 review).
        await using TradingCopilotDbContext context = Context();
        NewsIngestionService first = Service(
            context, Source(Finnhub, [Item("https://finnhub.example/apple", "Apple unveils the iPhone 17 at its fall event", "AAPL")]));
        await first.IngestAsync(Now, CancellationToken.None);

        NewsIngestionService second = Service(
            context, Source(Tiingo, [Item("https://tiingo.example/aapl", "Apple unveils iPhone 17", "AAPL", "SPY")]));
        await second.IngestAsync(Now.AddMinutes(2), CancellationToken.None);

        (await context.News.SingleAsync()).Tickers.Should().BeEquivalentTo(["AAPL", "SPY"]);
    }

    [Fact]
    public async Task IngestAsync_ShouldAdoptTheFullerHeadline_WhenAFuzzyMergeReceivesAStrictSuperset()
    {
        // "First feed wins its content" kept the POORER title whenever the later feed carried the fuller re-headline --
        // strictly worse than the pre-PR two-rows outcome. On a fuzzy merge the survivor adopts the incoming
        // title/summary when its content strictly contains the survivor's (gh#827 review).
        await using TradingCopilotDbContext context = Context();
        NewsIngestionService first = Service(
            context, Source(Finnhub, [Item("https://finnhub.example/apple", "Apple unveils iPhone 17", "AAPL")]));
        await first.IngestAsync(Now, CancellationToken.None);

        NewsIngestionService second = Service(
            context, Source(Tiingo, [Item("https://tiingo.example/aapl", "Apple unveils the iPhone 17 at its fall event", "AAPL")]));
        await second.IngestAsync(Now.AddMinutes(2), CancellationToken.None);

        (await context.News.SingleAsync()).Title.Should().Be(
            "Apple unveils the iPhone 17 at its fall event", "a fuzzy merge adopts a strictly fuller headline");
    }

    [Fact]
    public async Task IngestAsync_ShouldUnionProvenanceAcrossPasses_WhenTheSameStoryArrivesUnderADifferentUrlLater()
    {
        // The cross-pass half of the fallback: pass one stores the story from Finnhub under URL A; a later pass sees
        // Tiingo carry it under a DIFFERENT URL B. Canonical dedup misses (different key), so the fuzzy fallback must
        // match the already-stored row and union provenance rather than store a second row.
        await using TradingCopilotDbContext context = Context();
        NewsIngestionService first = Service(
            context, Source(Finnhub, [Item("https://finnhub.example/apple", "Apple unveils the iPhone 17 at its fall event", "AAPL")]));
        await first.IngestAsync(Now, CancellationToken.None);

        NewsIngestionService second = Service(
            context, Source(Tiingo, [Item("https://tiingo.example/aapl", "Apple unveils iPhone 17", "AAPL")]));
        await second.IngestAsync(Now.AddMinutes(2), CancellationToken.None);

        (await context.News.CountAsync()).Should().Be(1, "the same story under a different URL a pass later must not duplicate");
        (await context.News.SingleAsync()).SourceFeeds.Should().BeEquivalentTo(["finnhub", "tiingo"]);
    }

    [Fact]
    public async Task IngestAsync_ShouldUseTheStoredOriginalAsAFuzzyCandidate_WhenAnOverlappingPollCarriesItWithoutTickers()
    {
        // gh#827 Bugbot regression. Pass one stores Finnhub's original URL with AAPL. The next overlapping poll
        // carries that original URL again but WITH incomplete ticker metadata plus Tiingo's different-URL copy with
        // AAPL. Within-pass fuzzy cannot join them (the current original has no ticker), so the stored row from pass
        // one must remain visible to the cross-pass fuzzy lookup even though its canonical key is also pending; if it
        // is filtered out, Tiingo's copy inserts a second NewsRecord.
        await using TradingCopilotDbContext context = Context();
        const string originalUrl = "https://finnhub.example/apple";
        NewsIngestionService first = Service(
            context, Source(Finnhub, [Item(originalUrl, "Apple unveils the iPhone 17 at its fall event", "AAPL")]));
        await first.IngestAsync(Now, CancellationToken.None);

        INewsSource replayedOriginalWithoutTickers = Source(
            Finnhub, [Item(originalUrl, "Apple unveils the iPhone 17 at its fall event")]);
        INewsSource differentUrl = Source(
            Tiingo, [Item("https://tiingo.example/aapl", "Apple unveils iPhone 17", "AAPL")]);
        await Service(context, replayedOriginalWithoutTickers, differentUrl).IngestAsync(Now.AddMinutes(2), CancellationToken.None);

        (await context.News.CountAsync()).Should().Be(
            1, "the stored original must remain a fuzzy candidate even when its canonical URL is pending this pass");
        (await context.News.SingleAsync()).SourceFeeds.Should().BeEquivalentTo(["finnhub", "tiingo"]);
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
