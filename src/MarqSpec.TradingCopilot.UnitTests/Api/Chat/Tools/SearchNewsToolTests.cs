using System.Text.Json;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// The <c>search_news</c> chat tool (gh#987, R-6, ADR-0025) — since gh#995 a <b>thin adapter</b> over the shared
/// <see cref="IContextRetrievalService"/> pipeline. These pin the <i>adapter</i> contract: parse the model's JSON
/// input, ask the pipeline for <b>news only</b> (gh#1065 made it cross-kind; this tool's name and description are a
/// contract with the model about what it returns), and serialise its items into the compact model-facing result
/// shape — plus the fail-closed behaviour (a malformed / query-less input, or an unexpected pipeline throw, becomes
/// a compact error string; a genuine caller cancellation propagates). The retrieval pipeline itself — recall /
/// rerank / degrade / fail-open ledger — is faked here and proven in <c>ContextRetrievalServiceTests</c>.
/// </summary>
public class SearchNewsToolTests
{
    private static readonly DateTimeOffset _published = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);

    private readonly IContextRetrievalService _retrieval = A.Fake<IContextRetrievalService>();

    private SearchNewsTool Tool() => new(_retrieval, NullLogger<SearchNewsTool>.Instance);

    private static RetrievedContextItem Item(string headline, string snippet, params string[] sourceFeeds) =>
        new(RetrievalKind.News, headline, sourceFeeds.Length == 0 ? ["finnhub"] : [.. sourceFeeds], _published, snippet);

    private void RetrievalReturns(params RetrievedContextItem[] items) =>
        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._, A<int>._, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._))
            .Returns((IReadOnlyList<RetrievedContextItem>)[.. items]);

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

        A.CallTo(() => _retrieval.RetrieveAsync(
                "fed decision", 2, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("{\"query\":\"x\"}", 5)] // default when limit is absent
    [InlineData("{\"query\":\"x\",\"limit\":999}", 20)] // clamped to the max
    [InlineData("{\"query\":\"x\",\"limit\":0}", 1)] // clamped to the min
    public async Task ExecuteAsync_ShouldClampTheLimit_BeforeCallingTheService(string input, int expectedK)
    {
        RetrievalReturns(Item("Alpha", "s"));

        await Tool().ExecuteAsync(input, CancellationToken.None);

        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._, expectedK, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
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
        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._, A<int>._, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenTheServiceThrowsUnexpectedly()
    {
        // The pipeline is contracted to degrade to empty; a contract-violating throw must fail CLOSED (an error string
        // the model reads), never escape the tool.
        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._, A<int>._, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("pgvector exploded"));

        string result = await Tool().ExecuteAsync("{\"query\":\"x\"}", CancellationToken.None);

        Parse(result).GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._, A<int>._, A<IReadOnlyCollection<RetrievalKind>>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException());

        Func<Task> act = () => Tool().ExecuteAsync("{\"query\":\"x\"}", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAskThePipelineForNewsOnly_NeverTheOperatorsOwnRows()
    {
        // gh#1065 made the pipeline cross-kind. This tool is the model's NEWS search: its name and its description
        // promise headlines and source feeds, so quietly widening it to the trader's suggestions and journal entries
        // would both break that promise and hand the model rows it did not ask for.
        RetrievalReturns(Item("NVDA beats", "revenue up 40%"));

        await Tool().ExecuteAsync("""{"query":"nvidia"}""", CancellationToken.None);

        A.CallTo(() => _retrieval.RetrieveAsync(
                A<string>._,
                A<int>._,
                A<IReadOnlyCollection<RetrievalKind>>.That.IsSameSequenceAs(RetrievalKind.News),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }
}
