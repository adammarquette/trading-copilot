using System.Globalization;
using System.Net;
using System.Text;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The Cohere rerank provider (gh#975) behind the <see cref="IReranker"/> seam. Two jobs it must never get wrong: it
/// <b>degrades to passthrough (identity order) rather than throwing</b> on any provider trouble (a retrieval path
/// cannot take an exception from a rate limit), and it <b>meters every call it makes</b> — an unmetered rerank is
/// invisible spend on the operator's own key. It mirrors <see cref="CohereEmbeddingProvider"/> beat for beat.
/// </summary>
/// <remarks>No key and no network: every test drives a stubbed transport (engineering §5).</remarks>
public class CohereRerankProviderTests
{
    private const string Key = "cohere-key-not-a-secret";

    private readonly RecordingRerankMetrics _metrics = new();

    private static CohereOptions Configured() => new() { ApiKey = Key };

    private CohereRerankProvider Provider(StubHandler handler, CohereOptions? options = null) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(options ?? Configured()),
            _metrics,
            NullLogger<CohereRerankProvider>.Instance);

    private static IReadOnlyList<string> Docs(int count) =>
        Enumerable.Range(0, count).Select(i => $"doc-{i}").ToList();

    // A rerank response: the provider's ranked (index, relevance_score) pairs plus a billed search-unit count.
    private static string RankedResponse(int searchUnits, params (int Index, double Score)[] results)
    {
        string ranked = string.Join(",", results.Select(r =>
            $"{{\"index\":{r.Index},\"relevance_score\":{r.Score.ToString(CultureInfo.InvariantCulture)}}}"));
        return "{\"results\":[" + ranked + "],\"meta\":{\"billed_units\":{\"search_units\":" + searchUnits + "}}}";
    }

    // A well-formed success that reranks three candidates into the order 2, 0, 1.
    private static string ReorderedResponse() =>
        RankedResponse(1, (2, 0.75), (0, 0.5), (1, 0.25));

    // --- Availability & the unset key ---

    [Fact]
    public void IsAvailable_ShouldBeTrue_WhenAKeyIsConfigured() =>
        Provider(new StubHandler()).IsAvailable.Should().BeTrue();

    [Fact]
    public void IsAvailable_ShouldBeFalse_WhenNoKeyIsConfigured() =>
        Provider(new StubHandler(), new CohereOptions { ApiKey = null }).IsAvailable.Should().BeFalse();

    [Fact]
    public void Model_ShouldBeTheConfiguredRerankModel() =>
        Provider(new StubHandler(), new CohereOptions { ApiKey = Key, RerankModel = "rerank-multilingual-v3.0" })
            .Model.Should().Be("rerank-multilingual-v3.0");

    [Fact]
    public async Task RerankAsync_ShouldReturnPassthrough_WhenNotConfigured()
    {
        StubHandler handler = new() { Body = ReorderedResponse() };

        RerankResult result = await Provider(handler, new CohereOptions { ApiKey = " " })
            .RerankAsync("q", Docs(3), topN: 3, CancellationToken.None);

        result.Ranking.Select(r => r.Index).Should().Equal(new[] { 0, 1, 2 }, "an unconfigured provider degrades to identity order");
        handler.Requests.Should().BeEmpty("an unconfigured provider must not reach the network");
        _metrics.Records.Should().BeEmpty("a guard that never calls the provider is not a metered call");
    }

    // --- The happy path: rank echo, reorder, score mapping ---

    [Fact]
    public async Task RerankAsync_ShouldEchoTheProviderRankingAndScores_WhenCohereSucceeds()
    {
        StubHandler handler = new() { Body = ReorderedResponse() };

        RerankResult result = await Provider(handler).RerankAsync("Fed decision", Docs(3), topN: 3, CancellationToken.None);

        result.Outcome.Should().Be(RerankOutcome.Reranked);
        result.Ranking.Select(r => r.Index).Should().Equal(new[] { 2, 0, 1 }, "the ranked order is the provider's, not the input's");
        result.Ranking.Select(r => r.RelevanceScore).Should().Equal(0.75, 0.5, 0.25);
    }

    [Fact]
    public async Task RerankAsync_ShouldSendTheKeyAsABearerToken_AndNeverInTheBody()
    {
        StubHandler handler = new() { Body = ReorderedResponse() };

        await Provider(handler).RerankAsync("query text", Docs(2), topN: 2, CancellationToken.None);

        handler.LastRequest!.Headers.Authorization!.ToString().Should().Be($"Bearer {Key}");
        handler.LastBody.Should().NotContain(Key, "the key authenticates the request; it is never request content");
    }

    [Fact]
    public async Task RerankAsync_ShouldPostTheQueryDocumentsModelAndTopN_ToTheRerankEndpoint()
    {
        StubHandler handler = new() { Body = ReorderedResponse() };

        await Provider(handler, new CohereOptions { ApiKey = Key, RerankModel = "rerank-english-v3.0" })
            .RerankAsync("Fed decision", Docs(3), topN: 2, CancellationToken.None);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/v2/rerank");
        handler.LastBody.Should().Contain("\"model\":\"rerank-english-v3.0\"");
        handler.LastBody.Should().Contain("\"query\":\"Fed decision\"");
        handler.LastBody.Should().Contain("\"top_n\":2", "the requested top-n is the provider's to honour");
        handler.LastBody.Should().Contain("doc-0").And.Contain("doc-2");
    }

    [Fact]
    public async Task RerankAsync_ShouldHonorTopN_ByReturningOnlyWhatTheProviderRanked()
    {
        // Cohere applies top_n and returns only the top results; the provider echoes exactly that set.
        StubHandler handler = new() { Body = RankedResponse(1, (2, 0.9), (0, 0.4)) };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(5), topN: 2, CancellationToken.None);

        result.Ranking.Should().HaveCount(2);
        result.Ranking.Select(r => r.Index).Should().Equal(2, 0);
    }

    [Fact]
    public async Task RerankAsync_ShouldDropAnOutOfRangeIndex_RatherThanFabricateACandidate()
    {
        // A provider that echoes an index outside the candidate set is a bad shape for THAT entry; drop it defensively
        // rather than emit a RankedDocument that points nowhere, but keep the rest of the ranking.
        StubHandler handler = new() { Body = RankedResponse(1, (0, 0.9), (7, 0.8), (1, 0.2)) };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(2), topN: 3, CancellationToken.None);

        result.Ranking.Select(r => r.Index).Should().Equal(new[] { 0, 1 }, "index 7 is outside the 2-candidate set and is dropped");
    }

    // --- Degrade to passthrough rather than throwing (the core safety property) ---

    [Fact]
    public async Task RerankAsync_ShouldDegradeToPassthrough_WhenRateLimited()
    {
        StubHandler handler = new() { Status = HttpStatusCode.TooManyRequests };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(3), topN: 3, CancellationToken.None);

        result.Outcome.Should().Be(RerankOutcome.RateLimited);
        result.Ranking.Select(r => r.Index).Should().Equal(new[] { 0, 1, 2 }, "a 429 degrades to identity order — it must not throw");
    }

    [Fact]
    public async Task RerankAsync_ShouldDegradeToPassthrough_WhenCohereReturnsAServerError()
    {
        StubHandler handler = new() { Status = HttpStatusCode.InternalServerError };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(3), topN: 3, CancellationToken.None);

        result.Outcome.Should().Be(RerankOutcome.Failed);
        result.Ranking.Select(r => r.Index).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task RerankAsync_ShouldDegradeToPassthrough_WhenTheTransportThrows()
    {
        StubHandler handler = new() { Throw = new HttpRequestException("connection reset") };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(2), topN: 2, CancellationToken.None);

        result.Outcome.Should().Be(RerankOutcome.Failed);
        result.Ranking.Select(r => r.Index).Should().Equal(0, 1);
    }

    [Fact]
    public async Task RerankAsync_ShouldDegradeToPassthrough_WhenTheResponseBodyIsUnparseable()
    {
        StubHandler handler = new() { Body = "not json" };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(2), topN: 2, CancellationToken.None);

        result.Outcome.Should().Be(RerankOutcome.Failed);
        result.Ranking.Select(r => r.Index).Should().Equal(0, 1);
    }

    [Fact]
    public async Task RerankAsync_ShouldDegradeToPassthrough_WhenTheResponseCarriesNoResults()
    {
        // A 200 whose body has no `results` array is a bad shape — treat it like any other failure, identity order.
        StubHandler handler = new() { Body = "{\"meta\":{\"billed_units\":{\"search_units\":1}}}" };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(3), topN: 3, CancellationToken.None);

        result.Outcome.Should().Be(RerankOutcome.Failed);
        result.Ranking.Select(r => r.Index).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task RerankAsync_ShouldDegradeToPassthrough_WhenEveryResultIndexIsOutOfRange()
    {
        // review #980: results that ALL point outside the candidate set map to NO candidate -- a bad shape for the
        // WHOLE set (like a null body), not a partial drop. It must degrade to identity order and a zero-cost fault,
        // NOT emit an empty ranking silently billed as a success (the "never a dropped candidate set" guarantee).
        StubHandler handler = new() { Body = RankedResponse(3, (7, 0.9), (8, 0.8)) };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(3), topN: 3, CancellationToken.None);

        result.Outcome.Should().Be(RerankOutcome.Failed);
        result.Ranking.Select(r => r.Index).Should().Equal(new[] { 0, 1, 2 }, "every returned index is out of range -> passthrough, not an empty success");
        result.BilledSearches.Should().Be(0, "a bad-shape response is a zero-cost degrade, not a billed success");
    }

    [Fact]
    public async Task RerankAsync_ShouldTruncatePassthroughToTopN_OnADegrade()
    {
        // The identity fallback still honours the caller's top-n: it asked for 2, a degrade returns the first 2.
        StubHandler handler = new() { Status = HttpStatusCode.InternalServerError };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(5), topN: 2, CancellationToken.None);

        result.Ranking.Select(r => r.Index).Should().Equal(0, 1);
    }

    [Fact]
    public async Task RerankAsync_ShouldReturnAnEmptyRanking_WhenThereAreNoCandidates()
    {
        // Nothing to rank: an empty passthrough that never reaches the network and is not billed or metered — the same
        // no-call posture as the unconfigured guard (a call that never happened is not a metered call).
        StubHandler handler = new() { Body = ReorderedResponse() };

        RerankResult result = await Provider(handler).RerankAsync("q", [], topN: 5, CancellationToken.None);

        result.Ranking.Should().BeEmpty();
        handler.Requests.Should().BeEmpty("there is nothing to rerank, so the provider must not be called");
        _metrics.Records.Should().BeEmpty("a no-op with no candidates is not a metered call");
    }

    [Fact]
    public async Task RerankAsync_ShouldReturnAnEmptyRanking_WhenTopNIsZeroOrNegative()
    {
        StubHandler handler = new() { Body = ReorderedResponse() };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(3), topN: 0, CancellationToken.None);

        result.Ranking.Should().BeEmpty();
        handler.Requests.Should().BeEmpty("a request for zero results makes no provider call");
        _metrics.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task RerankAsync_ShouldRethrow_WhenTheCallerCancels()
    {
        StubHandler handler = new() { Throw = new OperationCanceledException() };
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Func<Task> act = () => Provider(handler).RerankAsync("q", Docs(2), topN: 2, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a genuine caller cancellation is host shutdown, not a provider failure to swallow");
    }

    // --- Metering: every call it makes, including a degrade (an unmetered call is invisible spend) ---

    [Fact]
    public async Task RerankAsync_ShouldMeterSearchesAndCost_WhenTheCallSucceeds()
    {
        // Cohere rerank bills per search: one call is one search unit, priced at the pinned $2.00 / 1000 searches.
        StubHandler handler = new() { Body = RankedResponse(1, (0, 0.9)) };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(1), topN: 1, CancellationToken.None);

        RecordedRerank rerank = _metrics.Records.Should().ContainSingle().Subject;
        rerank.Outcome.Should().Be(RerankOutcome.Reranked);
        rerank.BilledSearches.Should().Be(1);
        rerank.EstimatedCostUsd.Should().Be(0.002m, "one search at the pinned $2.00 / 1000-searches rate");
        rerank.Model.Should().Be("rerank-english-v3.0");

        // The SAME facts ride the return value so a consumer ledgers exactly what was metered (the gh#377 posture).
        result.Outcome.Should().Be(RerankOutcome.Reranked);
        result.BilledSearches.Should().Be(1);
        result.EstimatedCostUsd.Should().Be(0.002m);
    }

    [Fact]
    public async Task RerankAsync_ShouldBillOneSearch_WhenASuccessCarriesNoBilledUnits()
    {
        // A successful rerank consumed a search even if the provider omitted the billed-units block: default to 1
        // rather than under-count a call that plainly happened.
        StubHandler handler = new() { Body = "{\"results\":[{\"index\":0,\"relevance_score\":0.5}]}" };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(1), topN: 1, CancellationToken.None);

        result.Outcome.Should().Be(RerankOutcome.Reranked);
        result.BilledSearches.Should().Be(1);
        result.EstimatedCostUsd.Should().Be(0.002m);
    }

    [Fact]
    public async Task RerankAsync_ShouldMeterADegrade_AsRateLimitedWithZeroCost()
    {
        StubHandler handler = new() { Status = HttpStatusCode.TooManyRequests };

        RerankResult result = await Provider(handler).RerankAsync("q", Docs(3), topN: 3, CancellationToken.None);

        RecordedRerank rerank = _metrics.Records.Should().ContainSingle(
            "a call that degraded is still a call — leaving it unmetered would hide the degrade").Subject;
        rerank.Outcome.Should().Be(RerankOutcome.RateLimited);
        rerank.BilledSearches.Should().Be(0);
        rerank.EstimatedCostUsd.Should().Be(0m);
        rerank.Latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);

        result.Outcome.Should().Be(RerankOutcome.RateLimited);
        result.BilledSearches.Should().Be(0);
        result.EstimatedCostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task RerankAsync_ShouldMeterATransportFailure_AsFailed()
    {
        StubHandler handler = new() { Throw = new HttpRequestException("boom") };

        await Provider(handler).RerankAsync("q", Docs(2), topN: 2, CancellationToken.None);

        _metrics.Records.Should().ContainSingle().Which.Outcome.Should().Be(RerankOutcome.Failed);
    }

    [Fact]
    public async Task RerankAsync_ShouldMeterAServerError_AsFailed()
    {
        StubHandler handler = new() { Status = HttpStatusCode.BadGateway };

        await Provider(handler).RerankAsync("q", Docs(2), topN: 2, CancellationToken.None);

        _metrics.Records.Should().ContainSingle().Which.Outcome.Should().Be(RerankOutcome.Failed);
    }

    // --- Doubles ---

    private sealed record RecordedRerank(
        string Model, RerankOutcome Outcome, int BilledSearches, decimal EstimatedCostUsd, TimeSpan Latency);

    private sealed class RecordingRerankMetrics : IRerankMetrics
    {
        public List<RecordedRerank> Records { get; } = [];

        public void RecordRerank(string model, RerankOutcome outcome, int billedSearches, decimal estimatedCostUsd, TimeSpan latency) =>
            Records.Add(new RecordedRerank(model, outcome, billedSearches, estimatedCostUsd, latency));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;

        public StubHttpClientFactory(StubHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler) { BaseAddress = new Uri("https://api.cohere.test") };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = "{}";

        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            if (Throw is not null)
            {
                throw Throw;
            }

            return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
}
