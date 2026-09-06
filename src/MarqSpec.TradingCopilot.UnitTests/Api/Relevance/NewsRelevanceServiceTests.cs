using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Relevance;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Relevance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Relevance;

/// <summary>
/// The news-relevance resolution pass (gh#359): it materializes matched instruments / topics onto news, resolving
/// what is unresolved or stale-since-a-config-change, and re-running is idempotent.
/// </summary>
public class NewsRelevanceServiceTests
{
    private static DateTimeOffset Now => new(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();

    private readonly IEmbeddingProvider _provider = A.Fake<IEmbeddingProvider>();
    private readonly INewsEmbeddingSimilarity _similarity = A.Fake<INewsEmbeddingSimilarity>();
    private readonly Dictionary<string, IReadOnlyList<float>> _newsVectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<float>> _topicVectors = new(StringComparer.Ordinal);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    // A fake provider defaults to IsAvailable=false, so any test that does not call ProviderUp() resolves keyword-only
    // -- exactly the behaviour before gh#854, which leaves the pre-existing pass tests unchanged.
    private NewsRelevanceService Service(TradingCopilotDbContext context, ILogger<NewsRelevanceService>? logger = null) =>
        new(context, _provider, _similarity, logger ?? NullLogger<NewsRelevanceService>.Instance);

    private void EmbedNews(string dedupKey, params float[] vector) => _newsVectors[dedupKey] = vector;

    private void EmbedTopic(string name, params float[] vector) => _topicVectors[name] = vector;

    // Brings the provider up and makes the fake seam serve vectors from the two in-memory stores, mirroring the real
    // Where(ownerIds.Contains(OwnerId)) reads -- news vectors keyed by dedup key, topic vectors keyed by topic name.
    private void ProviderUp()
    {
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyCollection<string> ids, CancellationToken _) =>
            {
                IReadOnlyList<StoredEmbedding> hits =
                    [.. ids.Where(_newsVectors.ContainsKey).Select(id => new StoredEmbedding(id, _newsVectors[id]))];
                return hits;
            });
        A.CallTo(() => _similarity.GetTopicVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyCollection<string> names, CancellationToken _) =>
            {
                IReadOnlyList<StoredEmbedding> hits =
                    [.. names.Where(_topicVectors.ContainsKey).Select(name => new StoredEmbedding(name, _topicVectors[name]))];
                return hits;
            });
    }

    private static NewsTopic Topic(string name, params string[] keywords) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Keywords = [.. keywords],
        Scope = TopicScope.Global,
        Instrument = null,
    };

    private static NewsRecord News(string dedupKey, long? resolvedVersion, params string[] tickers) => new()
    {
        DedupKey = dedupKey,
        Type = "news",
        Url = $"https://site.com/{dedupKey}",
        Title = "FOMC holds rates",
        Summary = "The committee left rates unchanged.",
        PublishedAt = Now,
        Tickers = [.. tickers],
        SourceFeeds = ["finnhub"],
        RecordedAt = Now,
        RelevanceVersion = resolvedVersion,
    };

    private static RelevanceConfigState ConfigAt(long version) =>
        new() { Id = RelevanceConfigState.SingletonId, UpdatedAt = Now, Version = version };

    [Fact]
    public async Task ResolveAsync_ShouldMaterializeMatchesOntoNeverResolvedNews()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedVersion: null, "SPY")); // null version == never resolved
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(1);
        NewsRecord stored = await context.News.SingleAsync();
        stored.MatchedInstruments.Should().Contain("ES");
        stored.RelevanceVersion.Should().Be(0, "resolved against config generation 0 (no edits yet)");
        stored.RelevanceResolvedAt.Should().Be(Now, "the observability timestamp is still stamped");
    }

    [Fact]
    public async Task ResolveAsync_ShouldSkipNews_WhenItsVersionMatchesTheConfigGeneration()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.News.Add(News("a", resolvedVersion: 3, "SPY"));
            seed.RelevanceConfigStates.Add(ConfigAt(3));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(0, "the row is already resolved against the current generation");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReResolve_WhenTheConfigGenerationAdvanced()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedVersion: 4, "SPY")); // resolved against an older generation
            seed.RelevanceConfigStates.Add(ConfigAt(5));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(1);
        NewsRecord stored = await context.News.SingleAsync();
        stored.MatchedInstruments.Should().Contain("ES");
        stored.RelevanceVersion.Should().Be(5, "stamped with the generation it was resolved against");
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotDependOnWallClock_WhenTimeMovesBackwardsBetweenPasses()
    {
        // THE gh#418 GUARD. The old design compared timestamps, so a pass running with an EARLIER clock than a
        // prior resolution (clock skew across instances) could misjudge staleness. Version comparison cannot:
        // a config generation ahead of the row re-resolves regardless of what any clock says.
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedVersion: 1, "SPY"));
            seed.RelevanceConfigStates.Add(ConfigAt(2));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        // A clock a full day BEFORE the row's stored resolution time — nonsensical under a wall-clock scheme.
        int resolved = await Service(context).ResolveAsync(Now.AddDays(-1), CancellationToken.None);

        resolved.Should().Be(1, "generation 1 < 2, so it re-resolves — the clock is irrelevant");
    }

    [Fact]
    public async Task ResolveAsync_ShouldBeIdempotent()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedVersion: null, "SPY"));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        NewsRelevanceService service = Service(context);
        await service.ResolveAsync(Now, CancellationToken.None);
        int second = await service.ResolveAsync(Now.AddMinutes(1), CancellationToken.None);

        second.Should().Be(0); // nothing left to resolve — the version now matches
    }

    // --- Semantic topic match (gh#854): with the provider up, the pass reads the news + topic vectors over the seam
    // --- and matches a topic whose keywords are absent but whose vector is near the news vector. It degrades to
    // --- keyword-only on any read fault or an internal-token timeout, and never re-reads per item.

    [Fact]
    public async Task ResolveAsync_ShouldMaterializeASemanticTopic_WhenTheProviderIsUpAndTheNewsVectorIsNearTheTopicVector()
    {
        ProviderUp();
        EmbedTopic("sentiment", 1f, 0f); // the topic vector
        EmbedNews("a", 1f, 0f);          // the news vector -- identical direction -> cosine 1

        await using (TradingCopilotDbContext seed = Context())
        {
            seed.NewsTopics.Add(Topic("sentiment", "euphoria")); // keyword absent from the news text
            seed.News.Add(News("a", resolvedVersion: null));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(1);
        NewsRecord stored = await context.News.SingleAsync();
        stored.MatchedTopics.Should().Contain("sentiment", "the news vector is near the topic vector -> semantic match");
    }

    [Fact]
    public async Task ResolveAsync_ShouldResolveKeywordOnly_WithoutReadingTheSeam_WhenTheProviderIsUnavailable()
    {
        A.CallTo(() => _provider.IsAvailable).Returns(false);

        await using (TradingCopilotDbContext seed = Context())
        {
            seed.NewsTopics.Add(Topic("fomc", "FOMC")); // keyword present in the news title
            seed.News.Add(News("a", resolvedVersion: null));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        await Service(context).ResolveAsync(Now, CancellationToken.None);

        NewsRecord stored = await context.News.SingleAsync();
        stored.MatchedTopics.Should().Contain("fomc", "keyword matching is unaffected by the provider being down");
        A.CallTo(() => _similarity.GetTopicVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Theory]
    [MemberData(nameof(SeamFaults))]
    public async Task ResolveAsync_ShouldDegradeToKeywordOnly_WhenTheSeamFaults(Exception fault)
    {
        // A topic-vector read fault must degrade the pass to keyword-only, never error it -- relevance is core, the
        // semantic read is a soft add-on off the trading path.
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _similarity.GetTopicVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .Throws(fault);

        await using (TradingCopilotDbContext seed = Context())
        {
            seed.NewsTopics.Add(Topic("fomc", "FOMC"));
            seed.News.Add(News("a", resolvedVersion: null));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(1, "a semantic-read fault degrades to keyword-only; it must never error the pass");
        (await context.News.SingleAsync()).MatchedTopics.Should().Contain("fomc");
    }

    public static IEnumerable<object[]> SeamFaults() =>
    [
        [new InvalidOperationException("vector read failed")],
        [new DbUpdateException("pgvector is unavailable")],
    ];

    [Fact]
    public async Task ResolveAsync_ShouldDegradeToKeywordOnly_WhenTheSeamTimesOutOnAnInternalToken()
    {
        // gh#589: a downstream timeout surfaces as an OCE carrying an INTERNAL cancelled token while the CALLER's own
        // token stays healthy -- a read fault to degrade, not host shutdown. The `when (ct.IsCancellationRequested)`
        // filter (checking the caller's token) discriminates.
        using CancellationTokenSource internalTimeout = new();
        await internalTimeout.CancelAsync();
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _similarity.GetTopicVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException(internalTimeout.Token));

        await using (TradingCopilotDbContext seed = Context())
        {
            seed.NewsTopics.Add(Topic("fomc", "FOMC"));
            seed.News.Add(News("a", resolvedVersion: null));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(1, "a timeout OCE on an internal token degrades to keyword-only");
        (await context.News.SingleAsync()).MatchedTopics.Should().Contain("fomc");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRethrow_WhenTheCallerCancels()
    {
        // A genuine caller cancellation is host shutdown, not a fault to swallow. Cancel the caller's token AT the
        // seam call (not before), so the pass's earlier reads run on a healthy token and this actually exercises the
        // `when (cancellationToken.IsCancellationRequested)` rethrow -- rather than EF cancelling the very first read.
        using CancellationTokenSource cts = new();
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _similarity.GetTopicVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .Throws(_ =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            });

        await using (TradingCopilotDbContext seed = Context())
        {
            seed.NewsTopics.Add(Topic("fomc", "FOMC"));
            seed.News.Add(News("a", resolvedVersion: null));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        Func<Task> act = () => Service(context).ResolveAsync(Now, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReadTheNewsVectorsOncePerPage_NotPerItem()
    {
        // N+1 guard: one page (< BatchSize) reads the page's news vectors in ONE seam call and the topic vectors ONCE
        // per pass, never one read per news item.
        ProviderUp();

        await using (TradingCopilotDbContext seed = Context())
        {
            seed.NewsTopics.Add(Topic("sentiment", "euphoria"));
            seed.News.Add(News("a", resolvedVersion: null));
            seed.News.Add(News("b", resolvedVersion: null));
            seed.News.Add(News("c", resolvedVersion: null));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        await Service(context).ResolveAsync(Now, CancellationToken.None);

        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _similarity.GetTopicVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ResolveAsync_ShouldMatchViaLastWins_WhenTheSeamReturnsDuplicateOwnerIds()
    {
        // After a Cohere model change, (Topic, name, modelA) and (Topic, name, modelB) both persist, so the seam
        // returns two rows for one owner name. The last-wins collapse must tolerate that and still match -- a
        // ToDictionary would throw on the duplicate key and silently degrade the whole pass to keyword-only.
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        IReadOnlyList<StoredEmbedding> twoModelTopics =
            [new StoredEmbedding("sentiment", [0f, 1f]), new StoredEmbedding("sentiment", [1f, 0f])];
        IReadOnlyList<StoredEmbedding> newsHit = [new StoredEmbedding("a", [1f, 0f])];
        A.CallTo(() => _similarity.GetTopicVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .Returns(twoModelTopics);
        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .Returns(newsHit);

        await using (TradingCopilotDbContext seed = Context())
        {
            seed.NewsTopics.Add(Topic("sentiment", "euphoria")); // keyword absent -> only the semantic path can match
            seed.News.Add(News("a", resolvedVersion: null));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        await Service(context).ResolveAsync(Now, CancellationToken.None);

        NewsRecord stored = await context.News.SingleAsync();
        stored.MatchedTopics.Should().Contain(
            "sentiment", "the last-wins vector ([1,0]) matched the news vector — proving no throw and no degrade");
    }

    // --- Structurally-empty relevance input (gh#1124): a story with no provider ticker (Finnhub's symbol-less
    // --- `general` category) still resolves via the topic/keyword path over its headline + summary (R-2's
    // --- "ticker-map or topic" rule, already proven above by the *_WithNoInstrument tests carrying `[]` tickers).
    // --- The genuinely structural gap is a DEPLOYMENT with no ticker maps AND no topics curated at all: every
    // --- item then resolves to an empty match by construction, and the pass previously reported that as a plain
    // --- success (Debug-level, "N resolved") indistinguishable from "N resolved, all legitimately relevant to
    // --- nothing". That reads as "nothing was relevant" rather than "there is no configuration to match against" —
    // --- exactly the failure mode the card calls out — so an entirely empty config now logs loudly (Warning).

    [Fact]
    public async Task ResolveAsync_ShouldLogWarning_WhenNoMapsAndNoTopicsAreConfigured_SoNothingCanEverMatch()
    {
        ILogger<NewsRelevanceService> logger = A.Fake<ILogger<NewsRelevanceService>>();

        await using (TradingCopilotDbContext seed = Context())
        {
            // No TickerInstrumentMaps, no NewsTopics -- the config is entirely empty.
            seed.News.Add(News("a", resolvedVersion: null, "SPY")); // even a ticker present cannot help with no maps
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context, logger).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(1, "the item is still marked resolved -- the pass makes progress, it just cannot match");
        NewsRecord stored = await context.News.SingleAsync();
        stored.MatchedInstruments.Should().BeEmpty();
        stored.MatchedTopics.Should().BeEmpty();
        A.CallTo(logger).Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Warning)
            .MustHaveHappened(); // surfaced loudly -- a zero-match pass over an unconfigured relevance model is not a quiet success
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotLogWarning_WhenTopicsAreConfigured_EvenIfThisItemMatchesNone()
    {
        // A legitimate zero-match (a story that just isn't about any curated topic) is NOT the structural gap --
        // the config has a genuine input to match against, it simply didn't fire for this item. Only an entirely
        // unconfigured relevance model (no maps AND no topics) warrants the loud warning above.
        ILogger<NewsRelevanceService> logger = A.Fake<ILogger<NewsRelevanceService>>();

        await using (TradingCopilotDbContext seed = Context())
        {
            seed.NewsTopics.Add(Topic("crude-oil", "crude", "OPEC")); // configured, but absent from this story's text
            seed.News.Add(News("a", resolvedVersion: null)); // "FOMC holds rates" / "The committee left rates unchanged."
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        await Service(context, logger).ResolveAsync(Now, CancellationToken.None);

        (await context.News.SingleAsync()).MatchedTopics.Should().BeEmpty("this story does not mention the configured topic");
        // A configured-but-unmatched topic is a legitimate zero-match, not a structural gap -- no warning is owed.
        A.CallTo(logger).Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Warning)
            .MustNotHaveHappened();
    }
}
