using System.Text.Json;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// The <c>search_news</c> chat tool (gh#987, R-6, ADR-0025) — since gh#995 a <b>thin adapter</b> over the shared
/// <see cref="INewsRetrievalService"/> pipeline. These pin the <i>adapter</i> contract: parse the model's JSON input,
/// call the pipeline, and serialise its items into the compact model-facing result shape — plus the fail-closed
/// behaviour (a malformed / query-less input, or an unexpected pipeline throw, becomes a compact error string; a
/// genuine caller cancellation propagates). The retrieval pipeline itself — recall / rerank / degrade / fail-open
/// ledger — is faked here and proven in <c>NewsRetrievalServiceTests</c>.
/// </summary>
public class SearchNewsToolTests
{
    private static readonly DateTimeOffset _published = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);

    private readonly INewsRetrievalService _retrieval = A.Fake<INewsRetrievalService>();

    private SearchNewsTool Tool() => new(_retrieval, NullLogger<SearchNewsTool>.Instance);

    private static RetrievedNewsItem Item(string headline, string snippet, params string[] sourceFeeds) =>
        new(headline, sourceFeeds.Length == 0 ? ["finnhub"] : [.. sourceFeeds], _published, snippet);

    private void RetrievalReturns(params RetrievedNewsItem[] items) =>
        A.CallTo(() => _retrieval.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Returns((IReadOnlyList<RetrievedNewsItem>)[.. items]);

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
    public async Task ExecuteAsync_ShouldSerialiseTheServiceItems_InOrder_AsCompactResults()
    {
        RetrievalReturns(Item("Charlie", "s-c"), Item("Alpha", "s-a"), Item("Bravo", "s-b"));

        string result = await Tool().ExecuteAsync("{\"query\":\"nvidia earnings\"}", CancellationToken.None);

        // The adapter honours the pipeline's list order verbatim (it never re-sorts).
        Headlines(result).Should().Equal("Charlie", "Alpha", "Bravo");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSerialiseCompactFields_HeadlineSourcePublishedAtSnippet()
    {
        RetrievalReturns(Item("Alpha", "the summary body", "finnhub"));

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        JsonElement item = Parse(result).GetProperty("results")[0];
        item.GetProperty("headline").GetString().Should().Be("Alpha");
        item.GetProperty("source").GetString().Should().Be("finnhub");
        item.GetProperty("snippet").GetString().Should().Be("the summary body");
        item.TryGetProperty("publishedAt", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldJoinMultipleSourceFeeds_IntoTheSourceField()
    {
        RetrievalReturns(Item("Alpha", "s", "finnhub", "tiingo"));

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Parse(result).GetProperty("results")[0].GetProperty("source").GetString().Should().Be("finnhub, tiingo");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnEmptyResults_WhenTheServiceReturnsNone()
    {
        RetrievalReturns(); // the pipeline degraded to empty (unavailable / nothing near / rerank degrade)

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Parse(result).GetProperty("results").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassTheQuery_AndTheParsedClampedLimit_ToTheService()
    {
        RetrievalReturns(Item("Alpha", "s"));

        await Tool().ExecuteAsync("{\"query\":\"fed decision\",\"limit\":2}", CancellationToken.None);

        A.CallTo(() => _retrieval.RetrieveAsync("fed decision", 2, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("{\"query\":\"x\"}", 5)] // default when limit is absent
    [InlineData("{\"query\":\"x\",\"limit\":999}", 20)] // clamped to the max
    [InlineData("{\"query\":\"x\",\"limit\":0}", 1)] // clamped to the min
    public async Task ExecuteAsync_ShouldClampTheLimit_BeforeCallingTheService(string input, int expectedK)
    {
        RetrievalReturns(Item("Alpha", "s"));

        await Tool().ExecuteAsync(input, CancellationToken.None);

        A.CallTo(() => _retrieval.RetrieveAsync(A<string>._, expectedK, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_AndNeverCallTheService_WhenInputIsMalformedOrMissingQuery()
    {
        Parse(await Tool().ExecuteAsync("not json", CancellationToken.None))
            .GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        Parse(await Tool().ExecuteAsync("{}", CancellationToken.None))
            .GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        Parse(await Tool().ExecuteAsync("{\"query\":\"   \"}", CancellationToken.None))
            .GetProperty("error").GetString().Should().NotBeNullOrEmpty();

        // A malformed / query-less input is rejected in the adapter -- the pipeline is never reached.
        A.CallTo(() => _retrieval.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenTheServiceThrowsUnexpectedly()
    {
        // The pipeline is contracted to degrade to empty; a contract-violating throw must fail CLOSED (an error string
        // the model reads), never escape the tool.
        A.CallTo(() => _retrieval.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("pgvector exploded"));

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Parse(result).GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        A.CallTo(() => _retrieval.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException());

        Func<Task> act = () => Tool().ExecuteAsync("{\"query\":\"x\"}", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
