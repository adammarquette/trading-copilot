using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The shared <b>cross-kind</b> retrieval pipeline (gh#1065, R-6 / R-20, generalising gh#995): embed the query once →
/// recall each asked kind → hydrate → merge nearest-first → rerank. The recall / rerank seams are faked (FakeItEasy)
/// so the pipeline, its degrade paths, its R-20 owner scoping and its fail-open spend ledger are unit-testable without
/// a real Cohere key or pgvector. <c>NewsRecord</c> (global, R-20 exception), <c>Suggestion</c> and <c>Trade</c> (both
/// <c>IUserOwned</c>) are fully mapped in-memory, so the hydrate reads run behind a real, tenant-filtered
/// <see cref="TradingCopilotDbContext"/> — which is what makes the cross-owner cases below a proof rather than a mock
/// assertion.
/// </summary>
public class ContextRetrievalServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 1, 14, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();

    private readonly IEmbeddingProvider _embed = A.Fake<IEmbeddingProvider>();
    private readonly IEmbeddingRecall _recall = A.Fake<IEmbeddingRecall>();
    private readonly IReranker _reranker = A.Fake<IReranker>();
    private readonly IAiUsageLedger _ledger = A.Fake<IAiUsageLedger>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public ContextRetrievalServiceTests()
    {
        A.CallTo(() => _embed.IsAvailable).Returns(true);
        A.CallTo(() => _embed.Model).Returns("embed-english-v3.0");
        A.CallTo(() => _reranker.Model).Returns("rerank-english-v3.0");
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).Returns(Task.CompletedTask);

        // Every kind recalls nothing unless a test says otherwise, so a test that seeds one kind is not silently
        // carried by another kind's default fake return.
        A.CallTo(() => _recall.NearestAsync(A<RetrievalKind>._, A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<SemanticNeighbor>>([]);
    }

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context(Guid? asUser = null) => new(Options, new FixedUser(asUser ?? _owner));

    private ContextRetrievalService Service() => new(
        Context(),
        _embed,
        _recall,
        _reranker,
        _ledger,
        new FixedUser(_owner),
        TimeProvider.System,
        NullLogger<ContextRetrievalService>.Instance);

    private static NewsRecord News(string key, string title, string summary, params string[] sourceFeeds) => new()
    {
        DedupKey = key,
        Type = "news",
        Url = $"https://example.test/{key}",
        Title = title,
        Summary = summary,
        PublishedAt = _now,
        Tickers = [],
        SourceFeeds = sourceFeeds.Length == 0 ? ["finnhub"] : [.. sourceFeeds],
        RecordedAt = _now,
    };

    private static Suggestion Suggestion(Guid id, Guid owner, string instrument, string rationale) => new()
    {
        Id = id,
        UserId = owner,
        AccountId = Guid.NewGuid(),
        Instrument = instrument,
        Side = OrderSide.Buy,
        Size = 2,
        EntryPrice = 5_000.25m,
        StopPrice = 4_990.00m,
        TargetPrice = 5_020.50m,
        Mode = TradingMode.Practice,
        State = SuggestionState.Active,
        CreatedAt = _now,
        Rationale = rationale,
        Confidence = 70,
        ExpiresAt = _now.AddHours(1),
    };

    private static Trade Trade(Guid id, Guid owner, string instrument, decimal realized) => new()
    {
        Id = id,
        UserId = owner,
        AccountId = Guid.NewGuid(),
        Instrument = instrument,
        Side = OrderSide.Buy,
        Size = 2,
        EntryPrice = 5_000.25m,
        ExitPrice = 5_010.50m,
        RealizedPnL = realized,
        Mode = TradingMode.Practice,
        ClosedAt = _now,
    };

    private async Task SeedAsync(Action<TradingCopilotDbContext> seed)
    {
        // Seeded through a context scoped to the SEEDER, so a stranger's rows really are written under their owner --
        // the tenant filter is on reads, and a row must be genuinely foreign for the R-20 cases below to mean anything.
        await using TradingCopilotDbContext context = Context(_stranger);
        seed(context);
        await context.SaveChangesAsync();
    }

    private static EmbeddingResult Embedded() => new([0.1f, 0.2f, 0.3f], EmbeddingOutcome.Embedded, 8, 0.0002m);

    private void EmbedReturns(EmbeddingResult result) =>
        A.CallTo(() => _embed.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).Returns(result);

    // Recalls the given owner ids for one kind, at ascending cosine distances starting from `firstDistance`.
    private void RecallReturns(RetrievalKind kind, double firstDistance, params string[] ownerIds) =>
        A.CallTo(() => _recall.NearestAsync(kind, A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<SemanticNeighbor>>(
                [.. ownerIds.Select((id, position) => new SemanticNeighbor(id, firstDistance + (0.01 * position)))]);

    private void RecallReturns(RetrievalKind kind, params string[] ownerIds) => RecallReturns(kind, 0.1, ownerIds);

    // A fixed ranking over the merged candidate list; each tuple is (index into that list, provider relevance score).
    private void RerankReturns(RerankOutcome outcome, params (int Index, double Score)[] ranking) =>
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<string>>._, A<int>._, A<CancellationToken>._))
            .Returns(new RerankResult(
                [.. ranking.Select(entry => new RankedDocument(entry.Index, entry.Score))], outcome, 1, 0.0001m));

    // A passthrough (degraded) rerank: the identity order over however many candidates it was handed -- exactly what
    // UnavailableReranker returns with no Cohere key, so the pipeline's own merge order is what shows through.
    private void RerankDegrades() =>
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<string>>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, IReadOnlyList<string> documents, int topN, CancellationToken _) =>
                new RerankResult(
                    [.. Enumerable.Range(0, Math.Min(topN, documents.Count)).Select(index => new RankedDocument(index, 0d))],
                    RerankOutcome.Failed, 0, 0m));

    private static IReadOnlyList<string> Titles(IReadOnlyList<RetrievedContextItem> items) =>
        [.. items.Select(item => item.Title)];

    private Task<IReadOnlyList<RetrievedContextItem>> RetrieveAsync(
        string query = "how have my ES longs gone", int k = 5, IReadOnlyCollection<RetrievalKind>? kinds = null) =>
        Service().RetrieveAsync(query, k, kinds ?? RetrievalKinds.All, CancellationToken.None);

    [Fact]
    public async Task RetrieveAsync_ShouldReturnItemsInRerankListOrder_NotRecallOrderOrScoreOrder()
    {
        await SeedAsync(context => context.News.AddRange(
            News("a", "Alpha", "s-a"), News("b", "Bravo", "s-b"), News("c", "Charlie", "s-c")));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.News, "a", "b", "c");

        // List order [C, A, B]; the scores deliberately do NOT descend, so honouring the LIST order (the seam's
        // contract) yields [C, A, B] while wrongly re-sorting by score would yield [A, B, C].
        RerankReturns(RerankOutcome.Reranked, (2, 0.10), (0, 0.90), (1, 0.50));

        Titles(await RetrieveAsync()).Should().Equal("Charlie", "Alpha", "Bravo");
    }

    [Fact]
    public async Task RetrieveAsync_ShouldRetrieveEveryAskedKind_InOneCrossKindResult()
    {
        Guid suggestion = Guid.NewGuid();
        Guid trade = Guid.NewGuid();
        await SeedAsync(context =>
        {
            context.News.Add(News("a", "Alpha", "s-a"));
            context.Suggestions.Add(Suggestion(suggestion, _owner, "ES", "trend continuation"));
            context.Trades.Add(Trade(trade, _owner, "ES", 512.50m));
        });
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.News, 0.30, "a");
        RecallReturns(RetrievalKind.Suggestion, 0.10, suggestion.ToString());
        RecallReturns(RetrievalKind.JournalEntry, 0.20, trade.ToString());
        RerankDegrades();

        IReadOnlyList<RetrievedContextItem> items = await RetrieveAsync();

        // All three kinds present, and merged NEAREST-FIRST across kinds (0.10 suggestion, 0.20 journal, 0.30 news) --
        // the degraded reranker returns identity order, so the pipeline's own merge order is what is asserted here.
        items.Select(item => item.Kind).Should().Equal(
            RetrievalKind.Suggestion, RetrievalKind.JournalEntry, RetrievalKind.News);
    }

    [Fact]
    public async Task RetrieveAsync_ShouldRecallOnlyTheAskedKinds_WhenAConsumerAsksForOne()
    {
        await SeedAsync(context => context.News.Add(News("a", "Alpha", "s-a")));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.News, "a");
        RerankDegrades();

        await RetrieveAsync(kinds: [RetrievalKind.News]);

        A.CallTo(() => _recall.NearestAsync(RetrievalKind.News, A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _recall.NearestAsync(
                A<RetrievalKind>.That.Matches(kind => kind != RetrievalKind.News),
                A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RetrieveAsync_ShouldNeverReturnAnotherOperatorsSuggestion_WhenTheRecallCrossesOwners()
    {
        // The embedding store is deployment-global (not IUserOwned), so the vector recall legitimately returns BOTH
        // owners' suggestions. R-20 is enforced at the HYDRATE, through the tenant query filter -- so the stranger's
        // row must simply not be there. Both rows are seeded, and both are recalled, so a dropped owner filter would
        // return two items rather than one.
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        await SeedAsync(context => context.Suggestions.AddRange(
            Suggestion(mine, _owner, "ES", "my own rationale"),
            Suggestion(theirs, _stranger, "NQ", "a stranger's rationale")));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.Suggestion, 0.10, theirs.ToString(), mine.ToString());
        RerankDegrades();

        IReadOnlyList<RetrievedContextItem> items = await RetrieveAsync(kinds: [RetrievalKind.Suggestion]);

        items.Should().ContainSingle();
        items.Single().Snippet.Should().Be("my own rationale");
        items.Should().NotContain(item => item.Snippet.Contains("stranger"));
    }

    [Fact]
    public async Task RetrieveAsync_ShouldNeverReturnAnotherOperatorsJournalEntry_WhenTheRecallCrossesOwners()
    {
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        await SeedAsync(context => context.Trades.AddRange(
            Trade(mine, _owner, "ES", 512.50m),
            Trade(theirs, _stranger, "NQ", -900.00m)));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.JournalEntry, 0.10, theirs.ToString(), mine.ToString());
        RerankDegrades();

        IReadOnlyList<RetrievedContextItem> items = await RetrieveAsync(kinds: [RetrievalKind.JournalEntry]);

        items.Should().ContainSingle();
        items.Single().Title.Should().Contain("ES");
        items.Should().NotContain(item => item.Title.Contains("NQ"));
    }

    [Fact]
    public async Task RetrieveAsync_ShouldReturnEmpty_WhenEveryRecalledOwnerBelongsToAnotherOperator()
    {
        // The whole recall is foreign: the result is EMPTY, never a fabricated or partially-leaked one.
        Guid theirs = Guid.NewGuid();
        await SeedAsync(context => context.Suggestions.Add(Suggestion(theirs, _stranger, "NQ", "not mine")));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.Suggestion, 0.10, theirs.ToString());
        RerankDegrades();

        (await RetrieveAsync(kinds: [RetrievalKind.Suggestion])).Should().BeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_ShouldEmbedTheQueryOnce_HoweverManyKindsAreAsked()
    {
        EmbedReturns(Embedded());

        await RetrieveAsync();

        // One paid embed serves every kind's recall -- the query vector is kind-independent, so embedding per kind
        // would triple the operator's bill for the same vector.
        A.CallTo(() => _embed.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RetrieveAsync_ShouldReturnEmpty_AndNeverPayOrLedger_WhenEmbeddingIsUnavailable()
    {
        await SeedAsync(context => context.News.Add(News("a", "Alpha", "s")));
        A.CallTo(() => _embed.IsAvailable).Returns(false);

        (await RetrieveAsync()).Should().BeEmpty();
        A.CallTo(() => _embed.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task RetrieveAsync_ShouldReturnEmpty_ButStillLedgerTheEmbed_WhenTheQueryVectorIsNull()
    {
        await SeedAsync(context => context.News.Add(News("a", "Alpha", "s")));
        EmbedReturns(new EmbeddingResult(null, EmbeddingOutcome.RateLimited, 0, 0m));

        (await RetrieveAsync()).Should().BeEmpty();

        A.CallTo(() => _ledger.RecordAsync(
                A<AiUsageEntry>.That.Matches(entry => entry.Cost.Feature == AiUsageFeature.Embed), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _recall.NearestAsync(A<RetrievalKind>._, A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<string>>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RetrieveAsync_ShouldReturnEmpty_AndNeverRerank_WhenNoKindRecallsAnything()
    {
        EmbedReturns(Embedded());

        (await RetrieveAsync()).Should().BeEmpty();

        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<string>>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RetrieveAsync_ShouldReturnEmpty_WhenNoRecalledOwnerStillHasARow()
    {
        // An embedding can outlive its owner between the vector read and the hydrate (the orphan GC is periodic, not
        // synchronous) -- so a recalled-but-gone owner is dropped, never fabricated.
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.News, "gone-1");
        RecallReturns(RetrievalKind.Suggestion, Guid.NewGuid().ToString());
        RecallReturns(RetrievalKind.JournalEntry, Guid.NewGuid().ToString());
        RerankDegrades();

        (await RetrieveAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_ShouldDropAnUnparseableOwnerId_ForAGuidKeyedKind()
    {
        // OwnerId is text in the store so any key shape fits; a Guid-keyed kind must therefore tolerate a row whose
        // id is not a Guid (a hand-written or corrupted row) by dropping it, never by throwing mid-retrieval.
        Guid mine = Guid.NewGuid();
        await SeedAsync(context => context.Suggestions.Add(Suggestion(mine, _owner, "ES", "mine")));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.Suggestion, 0.10, "not-a-guid", mine.ToString());
        RerankDegrades();

        IReadOnlyList<RetrievedContextItem> items = await RetrieveAsync(kinds: [RetrievalKind.Suggestion]);

        items.Should().ContainSingle();
        items.Single().Snippet.Should().Be("mine");
    }

    [Fact]
    public async Task RetrieveAsync_ShouldKeepTheCrossKindDistanceOrder_WhenTheRerankerDegradesToPassthrough()
    {
        // No Cohere key => UnavailableReranker returns identity order. The pipeline's merge must therefore already be
        // meaningful on its own: nearest-first ACROSS kinds, not "all news, then all suggestions".
        Guid suggestion = Guid.NewGuid();
        await SeedAsync(context =>
        {
            context.News.AddRange(News("near", "NearNews", "s"), News("far", "FarNews", "s"));
            context.Suggestions.Add(Suggestion(suggestion, _owner, "ES", "middle"));
        });
        EmbedReturns(Embedded());
        A.CallTo(() => _recall.NearestAsync(RetrievalKind.News, A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<SemanticNeighbor>>([new("near", 0.05), new("far", 0.90)]);
        RecallReturns(RetrievalKind.Suggestion, 0.50, suggestion.ToString());
        RerankDegrades();

        IReadOnlyList<RetrievedContextItem> items = await RetrieveAsync();

        Titles(items).Should().HaveCount(3);
        items[0].Title.Should().Be("NearNews");
        items[1].Kind.Should().Be(RetrievalKind.Suggestion);
        items[2].Title.Should().Be("FarNews");
    }

    [Theory]
    [InlineData(2, 8)] // k * 4
    [InlineData(20, 50)] // k * 4 = 80, capped at 50
    [InlineData(50, 50)] // k * 4 = 200, capped at 50
    public async Task RetrieveAsync_ShouldRecall_KTimesFour_CappedAtFifty_PerKind(int k, int expectedRecall)
    {
        EmbedReturns(Embedded());

        await RetrieveAsync(k: k);

        A.CallTo(() => _recall.NearestAsync(
                A<RetrievalKind>._, A<IReadOnlyList<float>>._, expectedRecall, A<CancellationToken>._))
            .MustHaveHappened(RetrievalKinds.All.Count, Times.Exactly);
    }

    [Fact]
    public async Task RetrieveAsync_ShouldCapTheMergedCandidateSet_SoAddingKindsCannotGrowTheRerankPayload()
    {
        // Each kind recalls up to 50; three kinds would hand the reranker 150 documents. The merged set is capped at
        // the same ceiling, nearest-first, so the rerank payload does not grow with the number of kinds.
        EmbedReturns(Embedded());
        await SeedAsync(context => context.News.AddRange(
            [.. Enumerable.Range(0, 60).Select(index => News($"n{index}", $"News{index}", "s"))]));
        A.CallTo(() => _recall.NearestAsync(RetrievalKind.News, A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<SemanticNeighbor>>(
                [.. Enumerable.Range(0, 60).Select(index => new SemanticNeighbor($"n{index}", 0.01 * index))]);
        RerankDegrades();

        await RetrieveAsync(k: 20);

        A.CallTo(() => _reranker.RerankAsync(
                A<string>._, A<IReadOnlyList<string>>.That.Matches(documents => documents.Count == 50),
                A<int>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RetrieveAsync_ShouldPassKAsTheRerankTopN()
    {
        await SeedAsync(context => context.News.Add(News("a", "Alpha", "s")));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.News, "a");
        RerankDegrades();

        await RetrieveAsync(k: 2);

        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<string>>._, 2, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RetrieveAsync_ShouldProjectNews_AsHeadlineSourceFeedsPublishedAtSnippet()
    {
        await SeedAsync(context => context.News.Add(News("a", "Alpha", "the summary body", "finnhub", "tiingo")));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.News, "a");
        RerankDegrades();

        RetrievedContextItem item = (await RetrieveAsync(kinds: [RetrievalKind.News])).Single();

        item.Kind.Should().Be(RetrievalKind.News);
        item.Title.Should().Be("Alpha");
        item.Attribution.Should().Equal("finnhub", "tiingo"); // the raw feeds, for the consumer to render as it likes
        item.OccurredAt.Should().Be(_now);
        item.Snippet.Should().Be("the summary body");
    }

    [Fact]
    public async Task RetrieveAsync_ShouldProjectASuggestion_AsItsTradeLineAndRationale()
    {
        Guid id = Guid.NewGuid();
        await SeedAsync(context => context.Suggestions.Add(Suggestion(id, _owner, "ES", "trend continuation off the VWAP")));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.Suggestion, id.ToString());
        RerankDegrades();

        RetrievedContextItem item = (await RetrieveAsync(kinds: [RetrievalKind.Suggestion])).Single();

        item.Kind.Should().Be(RetrievalKind.Suggestion);
        item.Title.Should().Be("Suggested ES Buy 2 @ 5000.25 (stop 4990.00, target 5020.50)");
        item.Attribution.Should().Equal("Practice", "Active");
        item.OccurredAt.Should().Be(_now);
        item.Snippet.Should().Be("trend continuation off the VWAP"); // the model's rationale, untrusted display data
    }

    [Fact]
    public async Task RetrieveAsync_ShouldProjectAJournalEntry_AsItsClosedTradeLine()
    {
        Guid id = Guid.NewGuid();
        await SeedAsync(context => context.Trades.Add(Trade(id, _owner, "ES", 512.50m)));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.JournalEntry, id.ToString());
        RerankDegrades();

        RetrievedContextItem item = (await RetrieveAsync(kinds: [RetrievalKind.JournalEntry])).Single();

        item.Kind.Should().Be(RetrievalKind.JournalEntry);
        item.Title.Should().Be("Closed ES Buy 2 @ 5000.25 -> 5010.50");
        item.Attribution.Should().Equal("Practice");
        item.OccurredAt.Should().Be(_now);
        item.Snippet.Should().Be("Realized 512.50, a winner.");
    }

    [Fact]
    public async Task RetrieveAsync_ShouldRerankOnTheSameTextTheEmbedPassEmbedded_ForEveryKind()
    {
        // The reranker cross-encodes the query against the DOCUMENT text. If retrieval reranked a different rendering
        // than the embed pass embedded, the two stages would disagree about what each row says -- so both read the one
        // shared composer.
        Guid suggestion = Guid.NewGuid();
        Guid trade = Guid.NewGuid();
        Suggestion seededSuggestion = Suggestion(suggestion, _owner, "ES", "trend continuation");
        Trade seededTrade = Trade(trade, _owner, "ES", 512.50m);
        await SeedAsync(context =>
        {
            context.Suggestions.Add(seededSuggestion);
            context.Trades.Add(seededTrade);
        });
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.Suggestion, 0.10, suggestion.ToString());
        RecallReturns(RetrievalKind.JournalEntry, 0.20, trade.ToString());
        RerankDegrades();

        List<string> documents = [];
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<string>>._, A<int>._, A<CancellationToken>._))
            .Invokes((string _, IReadOnlyList<string> docs, int _, CancellationToken _) => documents.AddRange(docs))
            .ReturnsLazily((string _, IReadOnlyList<string> docs, int topN, CancellationToken _) =>
                new RerankResult(
                    [.. Enumerable.Range(0, Math.Min(topN, docs.Count)).Select(index => new RankedDocument(index, 0d))],
                    RerankOutcome.Failed, 0, 0m));

        await RetrieveAsync(kinds: [RetrievalKind.Suggestion, RetrievalKind.JournalEntry]);

        documents.Should().Equal(
            ContextEmbeddingContent.ForSuggestion(seededSuggestion),
            ContextEmbeddingContent.ForJournalEntry(seededTrade));
    }

    [Fact]
    public async Task RetrieveAsync_ShouldTruncateSnippet_ToTheCapWithAnEllipsis()
    {
        string longSummary = new('x', 300);
        await SeedAsync(context => context.News.Add(News("a", "Alpha", longSummary)));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.News, "a");
        RerankDegrades();

        RetrievedContextItem item = (await RetrieveAsync(kinds: [RetrievalKind.News])).Single();

        item.Snippet.Should().Be(new string('x', 240) + "…");
    }

    [Fact]
    public async Task RetrieveAsync_ShouldLedgerEmbedAndRerank_BothStampedToTheOperator()
    {
        await SeedAsync(context => context.News.Add(News("a", "Alpha", "s")));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.News, "a");
        RerankReturns(RerankOutcome.Reranked, (0, 0.9));

        List<AiUsageEntry> entries = [];
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Invokes((AiUsageEntry entry, CancellationToken _) => entries.Add(entry))
            .Returns(Task.CompletedTask);

        await RetrieveAsync();

        entries.Should().HaveCount(2, "one embed and one rerank, however many kinds were recalled");
        AiUsageEntry embed = entries.Single(entry => entry.Cost.Feature == AiUsageFeature.Embed);
        AiUsageEntry rerank = entries.Single(entry => entry.Cost.Feature == AiUsageFeature.Chat);

        embed.UserId.Should().Be(_owner);
        embed.Cost.Tier.Should().BeNull("an embed carries no model tier (ADR-0008)");
        embed.Cost.Outcome.Should().Be(AiUsageOutcome.Succeeded);

        rerank.UserId.Should().Be(_owner, "the retrieval rerank is stamped to the operator, not the deployment sentinel");
        rerank.Cost.Tier.Should().BeNull("rerank bills per search, with no model tier");
        rerank.Cost.Outcome.Should().Be(AiUsageOutcome.Succeeded);
    }

    [Fact]
    public async Task RetrieveAsync_ShouldStillReturnItems_WhenTheLedgerThrows()
    {
        await SeedAsync(context => context.News.Add(News("a", "Alpha", "s")));
        EmbedReturns(Embedded());
        RecallReturns(RetrievalKind.News, "a");
        RerankDegrades();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("ledger down"));

        Titles(await RetrieveAsync()).Should().Equal("Alpha");
    }

    [Fact]
    public async Task RetrieveAsync_ShouldPropagate_WhenARecallSeamThrowsUnexpectedly()
    {
        // The seams are CONTRACTED never to throw, so an unexpected throw is a real defect: it must surface at the
        // caller (which fails open for grounding, closed for the tool), never be hidden here as an empty result.
        EmbedReturns(Embedded());
        A.CallTo(() => _recall.NearestAsync(A<RetrievalKind>._, A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("pgvector gone"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RetrieveAsync());
    }

    [Fact]
    public async Task RetrieveAsync_ShouldReturnEmpty_AndNeverEmbed_WhenNoKindIsAsked()
    {
        EmbedReturns(Embedded());

        (await RetrieveAsync(kinds: [])).Should().BeEmpty();

        A.CallTo(() => _embed.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task RetrieveAsync_ShouldRefuse_WhenAskedForTheUnknownKind()
    {
        // The refusable zero is a caller error, not a degraded deployment -- returning nothing would hide the bug.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => RetrieveAsync(kinds: [RetrievalKind.Unknown]));
    }
}
