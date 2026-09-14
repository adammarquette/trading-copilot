using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.MarketData;

/// <summary>
/// Independent QA for the news-ingestion engine (gh#358, D2·1) — the deduped, multi-source store of record
/// (R-2, R-9, ADR-0014). Pre-merge tier: the real API pipeline over a throwaway Postgres container, two
/// adversarial <see cref="StubNewsSource"/> feeds at the <see cref="INewsSource"/> seam.
/// </summary>
/// <remarks>
/// Written from the spec (gh#360 acceptance + the data dictionary <c>NewsItem</c> row), not the implementation.
/// The stubs feed raw items; the engine computes the dedup key and the provenance feeds. Assertions are on the
/// persisted <see cref="NewsRecord"/> rows — the observable store of record — never on the engine's internals,
/// and the suite never calls the production <c>NewsDedupKey</c> to derive an expected value.
/// </remarks>
public sealed class NewsIngestionIntegrationTests : IClassFixture<NewsIngestionTestPostgresFactory>
{
    private static DateTimeOffset Now => new(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);

    private readonly NewsIngestionTestPostgresFactory _factory;

    public NewsIngestionIntegrationTests(NewsIngestionTestPostgresFactory factory)
    {
        _factory = factory;
        // Each test owns the store: the container is shared across the class, so start every test from empty.
        Finnhub().Items = [];
        Tiingo().Items = [];
        Finnhub().ThrowOnFetch = null;
        Tiingo().ThrowOnFetch = null;
        ClearNewsAsync().GetAwaiter().GetResult();
    }

    private StubNewsSource Finnhub() => _factory.Finnhub;

    private StubNewsSource Tiingo() => _factory.Tiingo;

    private static NewsItem Story(
        string url,
        string title = "Fed holds rates",
        string summary = "The FOMC left the target range unchanged.",
        DateTimeOffset? publishedAt = null,
        params string[] tickers) =>
        new(url, title, summary, publishedAt ?? Now.AddMinutes(-5), tickers.Length == 0 ? ["ES"] : tickers);

    // --- Two-source fan-in (gh#360: "seed both; assert both providers' items land") ---

    [Fact]
    public async Task IngestAsync_ShouldStoreItemsFromEverySource_WhenTheyCarryDistinctStories()
    {
        Finnhub().Items = [Story("https://example.com/finnhub-story", title: "From Finnhub")];
        Tiingo().Items = [Story("https://example.com/tiingo-story", title: "From Tiingo")];

        int written = await IngestAsync();

        written.Should().Be(2);
        List<NewsRecord> rows = await ReadAllAsync();
        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(r => r.SourceFeeds.Contains("finnhub"));
        rows.Should().ContainSingle(r => r.SourceFeeds.Contains("tiingo"));
    }

    // --- Dedup correctness (gh#360: same story → exactly one; distinct stay distinct) ---

    [Fact]
    public async Task IngestAsync_ShouldStoreOneRowWithBothFeeds_WhenBothSourcesCarryTheSameStory()
    {
        // The plain fan-in case: identical URL from both. One row, and provenance records BOTH feeds — that
        // union is how "the same story from both collapses to one" stays visible rather than silently losing a
        // source (data dictionary, ADR-0014).
        const string url = "https://example.com/the-same-story";
        Finnhub().Items = [Story(url, title: "Finnhub's headline")];
        Tiingo().Items = [Story(url, title: "Tiingo's headline")];

        await IngestAsync();

        NewsRecord row = (await ReadAllAsync()).Should().ContainSingle().Subject;
        row.SourceFeeds.Should().BeEquivalentTo(["finnhub", "tiingo"]);
    }

    [Fact]
    public async Task IngestAsync_ShouldTreatCanonicallyEquivalentUrlsAsOneStory()
    {
        // THE adversarial dedup guard. The two URLs are textually different but canonically the same story:
        // www prefix, a utm tracking parameter, and a trailing slash. A dedup that only matched exact strings
        // would store TWO rows here and pass every other test in this file. Feeding raw, differently-formatted
        // URLs is the only way to witness the canonicalisation.
        Finnhub().Items = [Story("https://www.example.com/markets/story?utm_source=newsletter")];
        Tiingo().Items = [Story("https://example.com/markets/story/")];

        await IngestAsync();

        NewsRecord row = (await ReadAllAsync()).Should().ContainSingle(
            "www, utm_* and a trailing slash are the same story — dedup is canonical, not string-equality").Subject;
        row.SourceFeeds.Should().BeEquivalentTo(["finnhub", "tiingo"]);
    }

    [Fact]
    public async Task IngestAsync_ShouldKeepDistinctStoriesDistinct_WhenUrlsGenuinelyDiffer()
    {
        // The other half of dedup correctness: no over-collapse. Two real stories from the same source must not
        // be merged just because they share a host.
        Finnhub().Items =
        [
            Story("https://example.com/story-one", title: "One"),
            Story("https://example.com/story-two", title: "Two"),
        ];

        int written = await IngestAsync();

        written.Should().Be(2);
        (await ReadAllAsync()).Should().HaveCount(2);
    }

    // --- Store of record (gh#360: idempotent re-ingest; persisted shape) ---

    [Fact]
    public async Task IngestAsync_ShouldNotCreateADuplicate_WhenTheSameStoryIsIngestedTwice()
    {
        Finnhub().Items = [Story("https://example.com/repolled")];

        await IngestAsync();
        int secondPass = await IngestAsync();

        secondPass.Should().Be(0, "an overlapping re-poll is idempotent — the second pass writes nothing new");
        (await ReadAllAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task IngestAsync_ShouldUnionProvenance_WhenASecondSourceLaterCarriesAKnownStory()
    {
        // The merge-into-EXISTING path, distinct from the in-pass dedup above. Pass 1: only Finnhub carries the
        // story. Pass 2: Tiingo now carries it too. Still one row, and its provenance must grow to include the
        // newly-seen feed rather than staying stuck at the first.
        const string url = "https://example.com/later-corroborated";
        Finnhub().Items = [Story(url)];
        await IngestAsync();

        Tiingo().Items = [Story(url)];
        await IngestAsync();

        NewsRecord row = (await ReadAllAsync()).Should().ContainSingle().Subject;
        row.SourceFeeds.Should().BeEquivalentTo(["finnhub", "tiingo"]);
    }

    [Fact]
    public async Task IngestAsync_ShouldPersistTheDataDictionaryShape()
    {
        DateTimeOffset published = Now.AddMinutes(-12);
        Finnhub().Items =
        [
            Story(
                "https://example.com/shaped",
                title: "Crude inventories drop",
                summary: "EIA reported a larger-than-expected draw.",
                publishedAt: published,
                tickers: ["CL", "RB"]),
        ];

        await IngestAsync();

        NewsRecord row = (await ReadAllAsync()).Should().ContainSingle().Subject;
        row.Url.Should().Be("https://example.com/shaped");
        row.Title.Should().Be("Crude inventories drop");
        row.Summary.Should().Be("EIA reported a larger-than-expected draw.");
        row.PublishedAt.Should().Be(published);
        row.Tickers.Should().BeEquivalentTo(["CL", "RB"]);
        row.SourceFeeds.Should().BeEquivalentTo(["finnhub"]);
        row.DedupKey.Should().NotBeNullOrWhiteSpace("the store of record is keyed by the dedup identity");
        row.Type.Should().NotBeNullOrWhiteSpace();
        row.RecordedAt.Should().NotBe(default);
    }

    // --- Robustness of the multi-source fan-in (ADR-0014: sources are independent) ---

    [Fact]
    public async Task IngestAsync_ShouldStillStoreAHealthySource_WhenAnotherSourceThrows()
    {
        // Multi-source only earns its cost if one bad feed cannot sink the others. A rate-limited Finnhub (the
        // common free-tier failure) must not cost Tiingo's story.
        Finnhub().ThrowOnFetch = new HttpRequestException("429 Too Many Requests");
        Tiingo().Items = [Story("https://example.com/survivor", title: "Still landed")];

        int written = await IngestAsync();

        written.Should().Be(1);
        NewsRecord row = (await ReadAllAsync()).Should().ContainSingle().Subject;
        row.SourceFeeds.Should().BeEquivalentTo(["tiingo"]);
    }

    [Fact]
    public async Task IngestAsync_ShouldDropAnItemWithNoUrl_RatherThanStoreAnUnmergeableRow()
    {
        // A URL is the dedup identity; an item without one could never merge with a later corroboration, so it
        // is dropped rather than stored. The healthy sibling in the same pass still lands.
        Finnhub().Items =
        [
            Story("", title: "No URL"),
            Story("https://example.com/has-url", title: "Has URL"),
        ];

        int written = await IngestAsync();

        written.Should().Be(1);
        NewsRecord row = (await ReadAllAsync()).Should().ContainSingle().Subject;
        row.Title.Should().Be("Has URL");
    }

    // --- R-2 fuzzy fallback (gh#764, continues gh#360, covers branch of #358) ---

    [Fact]
    public async Task IngestAsync_ShouldKeepSameFeedNearDuplicatesDistinct_WhenUrlsDiffer()
    {
        // The same-feed guard: two near-identical headlines from the SAME feed under different URLs are
        // distinct stories (a follow-up or a correction), not a duplicate to merge. Without the same-feed skip
        // in FindFuzzyMatch, unioning the feed onto the first would be a HashSet no-op that silently drops the
        // second row (gh#764 / #827 review). This guards that branch of the fuzzy dedup.
        DateTimeOffset published = Now.AddMinutes(-5);
        Finnhub().Items =
        [
            Story("https://example.com/story-v1", title: "Fed holds rates steady", publishedAt: published),
            Story("https://example.com/story-v2", title: "Fed holds rates steady amid inflation concerns", publishedAt: published),
        ];

        int written = await IngestAsync();

        written.Should().Be(2, "two near-identical stories from the same feed with different URLs stay distinct");
        List<NewsRecord> rows = await ReadAllAsync();
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.SourceFeeds.Should().BeEquivalentTo(["finnhub"]));
    }

    [Fact]
    public async Task IngestAsync_ShouldMergeCrossFeedNearDuplicates_WhenUrlsDifferButStoriesMatchFuzzy()
    {
        // The positive control for the same-feed guard above: the SAME pair of near-identical headlines from
        // TWO DIFFERENT feeds collapses to ONE row with both feeds in provenance. This proves the fuzzy matcher
        // itself is not broken — the first test cannot pass vacuously just because fuzzy matching is disabled
        // everywhere. The identical content and times ensure both tests' story pairs match the fuzzy rule.
        DateTimeOffset published = Now.AddMinutes(-5);
        Finnhub().Items =
        [
            Story("https://example.com/finnhub-v1", title: "Fed holds rates steady", publishedAt: published),
        ];
        Tiingo().Items =
        [
            Story("https://example.com/tiingo-v2", title: "Fed holds rates steady amid inflation concerns", publishedAt: published),
        ];

        int written = await IngestAsync();

        written.Should().Be(1, "the same near-identical story from two feeds collapses to one row");
        NewsRecord row = (await ReadAllAsync()).Should().ContainSingle().Subject;
        row.SourceFeeds.Should().BeEquivalentTo(["finnhub", "tiingo"], "both feeds are recorded in provenance");
    }

    // --- R-2 fuzzy fallback, ACROSS passes (gh#1132: FindFuzzyStored had no same-feed skip) ---

    [Fact]
    public async Task IngestAsync_ShouldKeepSameFeedNearDuplicatesDistinct_AcrossPasses()
    {
        // The cross-pass counterpart of the same-feed guard above: a follow-up (or correction) under a new URL
        // from the SAME feed, arriving in a LATER poll, is a distinct story, not a candidate for FindFuzzyStored
        // to fold into the row that pass 1 already stored. Pre-fix this either overwrites the first row's
        // Title/Summary (MergeFuzzyStored returns true) or silently drops the follow-up outright (returns false,
        // the write loop `continue`s regardless) — either way the desk loses the second story (gh#1132).
        DateTimeOffset published = Now.AddMinutes(-5);
        Finnhub().Items =
        [
            Story("https://example.com/cross-pass-v1", title: "Fed holds rates steady", publishedAt: published),
        ];
        await IngestAsync();

        Finnhub().Items =
        [
            Story(
                "https://example.com/cross-pass-v2",
                title: "Fed holds rates steady amid inflation concerns",
                publishedAt: published),
        ];
        int secondPass = await IngestAsync();

        secondPass.Should().Be(1, "the same-feed follow-up in a later poll is stored as its own row, not merged");
        List<NewsRecord> rows = await ReadAllAsync();
        rows.Should().HaveCount(2, "the original and the follow-up both survive as distinct rows");
        rows.Should().ContainSingle(r => r.Title == "Fed holds rates steady")
            .Which.Summary.Should().Be(
                "The FOMC left the target range unchanged.",
                "the first row's Title/Summary must be untouched by the later same-feed follow-up");
        rows.Should().ContainSingle(r => r.Title == "Fed holds rates steady amid inflation concerns");
        rows.Should().AllSatisfy(r => r.SourceFeeds.Should().BeEquivalentTo(["finnhub"]));
    }

    [Fact]
    public async Task IngestAsync_ShouldMergeCrossFeedNearDuplicates_AcrossPasses()
    {
        // The positive control for the guard above: a genuine CROSS-feed correction — a different feed's
        // near-identical rendering of the same story, arriving in a later poll — still collapses onto the row
        // pass 1 stored. This is what stops the same-feed fix from passing by simply disabling FindFuzzyStored
        // outright: the guard must be feed-scoped, not blanket.
        DateTimeOffset published = Now.AddMinutes(-5);
        Finnhub().Items =
        [
            Story("https://example.com/cross-pass-finnhub", title: "Fed holds rates steady", publishedAt: published),
        ];
        await IngestAsync();

        Tiingo().Items =
        [
            Story(
                "https://example.com/cross-pass-tiingo",
                title: "Fed holds rates steady amid inflation concerns",
                publishedAt: published),
        ];
        int secondPass = await IngestAsync();

        secondPass.Should().Be(1, "a cross-feed fuzzy match across passes still merges onto the stored row");
        NewsRecord row = (await ReadAllAsync()).Should().ContainSingle(
            "the cross-feed correction merges rather than duplicating").Subject;
        row.SourceFeeds.Should().BeEquivalentTo(["finnhub", "tiingo"], "both feeds are recorded in provenance");
    }

    // --- Helpers ---

    private async Task<int> IngestAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        NewsIngestionService service = scope.ServiceProvider.GetRequiredService<NewsIngestionService>();
        return await service.IngestAsync(Now, CancellationToken.None);
    }

    private async Task<List<NewsRecord>> ReadAllAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.News.AsNoTracking().ToListAsync();
    }

    private async Task ClearNewsAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.News.ExecuteDeleteAsync();
    }
}
