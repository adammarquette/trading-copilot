using System.Text.Json;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// The <c>search_news</c> chat tool (gh#987, R-6, ADR-0025 / ADR-0008): the first <see cref="IReranker"/> consumer — a
/// read-only semantic search over the ingested news feed (embed the query → nearest-news recall → hydrate → rerank).
/// The recall / rerank seams are faked (FakeItEasy) so the tool's pipeline, its degrade paths, and its fail-open
/// spend ledger are unit-testable without a real Cohere key or pgvector; <c>NewsRecord</c> is fully mapped in-memory
/// (it is <b>global</b>, not owner-scoped, R-20) so the hydrate read runs behind an in-memory
/// <see cref="TradingCopilotDbContext"/>.
/// </summary>
public class SearchNewsToolTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _owner = Guid.NewGuid();

    private readonly IEmbeddingProvider _embed = A.Fake<IEmbeddingProvider>();
    private readonly INewsEmbeddingSimilarity _similarity = A.Fake<INewsEmbeddingSimilarity>();
    private readonly IReranker _reranker = A.Fake<IReranker>();
    private readonly IAiUsageLedger _ledger = A.Fake<IAiUsageLedger>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public SearchNewsToolTests()
    {
        // A configured, available deployment by default; individual tests override the piece they exercise.
        A.CallTo(() => _embed.IsAvailable).Returns(true);
        A.CallTo(() => _embed.Model).Returns("embed-english-v3.0");
        A.CallTo(() => _reranker.Model).Returns("rerank-english-v3.0");
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).Returns(Task.CompletedTask);
    }

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context() => new(Options, new FixedUser(_owner));

    private SearchNewsTool Tool() => new(
        Context(),
        _embed,
        _similarity,
        _reranker,
        _ledger,
        new FixedUser(_owner),
        TimeProvider.System,
        NullLogger<SearchNewsTool>.Instance);

    private static NewsRecord News(string key, string title, string summary) => new()
    {
        DedupKey = key,
        Type = "news",
        Url = $"https://example.test/{key}",
        Title = title,
        Summary = summary,
        PublishedAt = _now,
        Tickers = [],
        SourceFeeds = ["finnhub"],
        RecordedAt = _now,
    };

    private async Task SeedAsync(params NewsRecord[] news)
    {
        await using TradingCopilotDbContext context = Context();
        context.News.AddRange(news);
        await context.SaveChangesAsync();
    }

    private static EmbeddingResult Embedded() => new([0.1f, 0.2f, 0.3f], EmbeddingOutcome.Embedded, 8, 0.0002m);

    private void EmbedReturns(EmbeddingResult result) =>
        A.CallTo(() => _embed.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).Returns(result);

    private void RecallReturns(params string[] dedupKeys) =>
        A.CallTo(() => _similarity.NearestNewsAsync(A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .Returns((IReadOnlyList<SemanticNeighbor>)
                [.. dedupKeys.Select((key, position) => new SemanticNeighbor(key, 0.1 * (position + 1)))]);

    // A fixed ranking over the recall list; each tuple is (index into the hydrated list, provider relevance score).
    private void RerankReturns(RerankOutcome outcome, params (int Index, double Score)[] ranking) =>
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<string>>._, A<int>._, A<CancellationToken>._))
            .Returns(new RerankResult(
                [.. ranking.Select(entry => new RankedDocument(entry.Index, entry.Score))], outcome, 1, 0.0001m));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static IReadOnlyList<string> Headlines(string json) =>
        [.. Parse(json).GetProperty("results").EnumerateArray().Select(item => item.GetProperty("headline").GetString()!)];

    [Fact]
    public void Definition_ShouldBeNamedSearchNews_AndRequireQuery()
    {
        IChatTool tool = Tool();

        tool.Name.Should().Be("search_news");
        tool.Definition.Name.Should().Be("search_news");
        tool.Definition.Description.Should().NotBeNullOrWhiteSpace();
        JsonElement schema = Parse(tool.Definition.InputSchema);
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("required")[0].GetString().Should().Be("query");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnNewsInRerankListOrder_NotRecallOrderOrScoreOrder()
    {
        await SeedAsync(News("a", "Alpha", "s-a"), News("b", "Bravo", "s-b"), News("c", "Charlie", "s-c"));
        EmbedReturns(Embedded());
        RecallReturns("a", "b", "c"); // recall order A, B, C

        // List order [C, A, B]; the scores deliberately do NOT descend, so honouring the LIST order (the seam's
        // contract) yields [C, A, B] while wrongly re-sorting by score would yield [A, B, C].
        RerankReturns(RerankOutcome.Reranked, (2, 0.10), (0, 0.90), (1, 0.50));

        string result = await Tool().ExecuteAsync("{\"query\":\"nvidia earnings\"}", CancellationToken.None);

        Headlines(result).Should().Equal("Charlie", "Alpha", "Bravo");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnCompactFields_HeadlineSourcePublishedAtSnippet()
    {
        await SeedAsync(News("a", "Alpha", "the summary body"));
        EmbedReturns(Embedded());
        RecallReturns("a");
        RerankReturns(RerankOutcome.Reranked, (0, 0.9));

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        JsonElement item = Parse(result).GetProperty("results")[0];
        item.GetProperty("headline").GetString().Should().Be("Alpha");
        item.GetProperty("source").GetString().Should().Be("finnhub");
        item.GetProperty("snippet").GetString().Should().Be("the summary body");
        item.TryGetProperty("publishedAt", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassTheLimitAsTopN_AndClampIt()
    {
        await SeedAsync(News("a", "Alpha", "s"), News("b", "Bravo", "s"));
        EmbedReturns(Embedded());
        RecallReturns("a", "b");
        RerankReturns(RerankOutcome.Reranked, (0, 0.9), (1, 0.8));

        await Tool().ExecuteAsync("{\"query\":\"x\",\"limit\":2}", CancellationToken.None);

        // The parsed, clamped limit is handed to the reranker as its top-n (gh#987: "IReranker.RerankAsync(query, docs, topN=limit)").
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<string>>._, 2, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnEmpty_AndNeverPayOrLedger_WhenEmbeddingIsUnavailable()
    {
        await SeedAsync(News("a", "Alpha", "s"));
        A.CallTo(() => _embed.IsAvailable).Returns(false);

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Parse(result).GetProperty("results").GetArrayLength().Should().Be(0);
        // No provider: no paid embed call and no spend row (mirrors NewsSemanticSearch's "no spend when unavailable").
        A.CallTo(() => _embed.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnEmpty_ButStillLedgerTheEmbed_WhenTheQueryVectorIsNull()
    {
        await SeedAsync(News("a", "Alpha", "s"));
        EmbedReturns(new EmbeddingResult(null, EmbeddingOutcome.RateLimited, 0, 0m)); // a rate-limited / failed embed

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Parse(result).GetProperty("results").GetArrayLength().Should().Be(0);
        // The attempted embed is ledgered honestly (an attempted call is spend), but no recall / rerank is attempted.
        A.CallTo(() => _ledger.RecordAsync(
            A<AiUsageEntry>.That.Matches(entry => entry.Cost.Feature == AiUsageFeature.Embed), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _similarity.NearestNewsAsync(A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<string>>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnEmpty_AndNeverRerank_WhenRecallIsEmpty()
    {
        await SeedAsync(News("a", "Alpha", "s"));
        EmbedReturns(Embedded());
        RecallReturns(); // an unavailable / faulting pgvector read degrades to empty by the seam's contract

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Parse(result).GetProperty("results").GetArrayLength().Should().Be(0);
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<string>>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnEmpty_WhenNoRecalledEmbeddingStillHasNews()
    {
        // The embeddings recall two owners, but neither dedup key has a NewsRecord (an embedding can outlive its
        // source news between the vector read and the hydrate) -- so there is nothing to show, never a throw.
        EmbedReturns(Embedded());
        RecallReturns("gone-1", "gone-2");

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Parse(result).GetProperty("results").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepRecallOrder_WhenTheRerankerDegradesToPassthrough()
    {
        await SeedAsync(News("a", "Alpha", "s"), News("b", "Bravo", "s"), News("c", "Charlie", "s"));
        EmbedReturns(Embedded());
        RecallReturns("a", "b", "c");

        // A degraded rerank returns the identity (recall) order truncated to top-n, with a Failed outcome -- the
        // seam guarantees this, so the tool simply reads the returned list order and never throws.
        RerankReturns(RerankOutcome.Failed, (0, 0d), (1, 0d), (2, 0d));

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Headlines(result).Should().Equal("Alpha", "Bravo", "Charlie");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldLedgerEmbedAndRerank_BothStampedToTheOperator()
    {
        await SeedAsync(News("a", "Alpha", "s"));
        EmbedReturns(Embedded());
        RecallReturns("a");
        RerankReturns(RerankOutcome.Reranked, (0, 0.9));

        List<AiUsageEntry> entries = [];
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Invokes((AiUsageEntry entry, CancellationToken _) => entries.Add(entry))
            .Returns(Task.CompletedTask);

        await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        entries.Should().HaveCount(2);
        AiUsageEntry embed = entries.Single(entry => entry.Cost.Feature == AiUsageFeature.Embed);
        AiUsageEntry rerank = entries.Single(entry => entry.Cost.Feature == AiUsageFeature.Chat);

        embed.UserId.Should().Be(_owner);
        embed.Cost.Tier.Should().BeNull("an embed carries no model tier (ADR-0008)");
        embed.Cost.Outcome.Should().Be(AiUsageOutcome.Succeeded);

        rerank.UserId.Should().Be(_owner, "the retrieval-tool rerank is stamped to the operator, not the deployment sentinel");
        rerank.Cost.Tier.Should().BeNull("rerank bills per search, with no model tier");
        rerank.Cost.Outcome.Should().Be(AiUsageOutcome.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStillReturnResults_WhenTheLedgerThrows()
    {
        await SeedAsync(News("a", "Alpha", "s"));
        EmbedReturns(Embedded());
        RecallReturns("a");
        RerankReturns(RerankOutcome.Reranked, (0, 0.9));

        // Fail-open: a bookkeeping fault must never fault the tool (IAiUsageLedger's contract, guarded at this boundary too).
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("ledger down"));

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Headlines(result).Should().Equal("Alpha");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenInputIsMalformedOrMissingQuery()
    {
        Parse(await Tool().ExecuteAsync("not json", CancellationToken.None))
            .GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        Parse(await Tool().ExecuteAsync("{}", CancellationToken.None))
            .GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        Parse(await Tool().ExecuteAsync("{\"query\":\"   \"}", CancellationToken.None))
            .GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenAReadFaultsUnexpectedly()
    {
        await SeedAsync(News("a", "Alpha", "s"));
        EmbedReturns(Embedded());
        // The similarity seam is contracted never to throw; a contract-violating throw must fail CLOSED (an error
        // string the model reads), never escape the tool.
        A.CallTo(() => _similarity.NearestNewsAsync(A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("pgvector exploded"));

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Parse(result).GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        A.CallTo(() => _embed.EmbedQueryAsync(A<string>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException());

        Func<Task> act = () => Tool().ExecuteAsync("{\"query\":\"x\"}", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
