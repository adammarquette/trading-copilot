using System.Net;
using System.Text;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The Cohere embedding provider (gh#403) behind the gh#109 <see cref="IEmbeddingProvider"/> seam. Two jobs it
/// must never get wrong: it <b>degrades to sparse rather than throwing</b> on any provider trouble (a retrieval
/// path cannot take an exception from a rate limit), and it <b>meters every call</b> — an unmetered embed is
/// invisible spend on the operator's own key.
/// </summary>
/// <remarks>No key and no network: every test drives a stubbed transport (engineering §5, gh#403 acceptance).</remarks>
public class CohereEmbeddingProviderTests
{
    private const string Key = "cohere-key-not-a-secret";

    private readonly RecordingEmbeddingMetrics _metrics = new();

    private static CohereOptions Configured() => new() { ApiKey = Key };

    private CohereEmbeddingProvider Provider(
        StubHandler handler, CohereOptions? options = null, VectorStore? store = null) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(options ?? Configured()),
            store ?? Present(),
            _metrics,
            NullLogger<CohereEmbeddingProvider>.Instance);

    /// <summary>A vector store that exists — the compose image bundles pgvector, so this is the ordinary case.</summary>
    private static VectorStore Present()
    {
        VectorStore store = new();
        store.Record(present: true);
        return store;
    }

    /// <summary>A Postgres without the extension: the gh#474 case, where the migration skipped the table.</summary>
    private static VectorStore Absent()
    {
        VectorStore store = new();
        store.Record(present: false);
        return store;
    }

    private static string EmbedResponse(int dimensions = 1024, int inputTokens = 7)
    {
        string vector = string.Join(",", Enumerable.Repeat("0.01", dimensions));
        return "{\"id\":\"x\",\"embeddings\":{\"float\":[[" + vector
            + "]]},\"meta\":{\"billed_units\":{\"input_tokens\":" + inputTokens + "}}}";
    }

    // --- Availability & the unset key ---

    [Fact]
    public void IsAvailable_ShouldBeTrue_WhenAKeyIsConfigured() =>
        Provider(new StubHandler()).IsAvailable.Should().BeTrue();

    [Fact]
    public void IsAvailable_ShouldBeFalse_WhenNoKeyIsConfigured() =>
        Provider(new StubHandler(), new CohereOptions { ApiKey = null }).IsAvailable.Should().BeFalse();

    [Fact]
    public void IsAvailable_ShouldBeFalse_WhenThereIsNowhereToStoreTheVector()
    {
        // gh#474. A key alone was enough to report available, so a Postgres WITHOUT pgvector — where the
        // AddEmbeddingStore migration deliberately skipped the table — passed this gate. The gh#377 pass then
        // embedded through Cohere on every poll (real spend, the operator's own key) and faulted at the upsert
        // every time: logged, harmless to trading, and never self-healing.
        //
        // "Available" has to mean the whole round trip is possible, not just the outbound half. The migration's
        // own remarks already promised this behaviour -- it was the code that did not have it.
        Provider(new StubHandler(), Configured(), Absent()).IsAvailable.Should().BeFalse(
            "embedding a vector that cannot be stored is spend with no product");
    }

    [Fact]
    public void IsAvailable_ShouldBeFalse_WhenTheStoreHasNotBeenProbedYet()
    {
        // Fail CLOSED before the startup probe runs. The alternative -- defaulting to available -- would spend on
        // any call that raced startup, which is the same defect in a narrower window.
        Provider(new StubHandler(), Configured(), new VectorStore()).IsAvailable.Should().BeFalse(
            "an unprobed store is an unknown store, and unknown is not a licence to spend");
    }

    [Fact]
    public void Dimensions_ShouldMatchThePgvectorColumnWidth() =>
        Provider(new StubHandler()).Dimensions.Should().Be(1024);

    [Fact]
    public async Task EmbedAsync_ShouldReturnNull_WhenNotConfigured()
    {
        StubHandler handler = new() { Body = EmbedResponse() };

        EmbeddingResult result = await Provider(handler, new CohereOptions { ApiKey = " " })
            .EmbedAsync("text", CancellationToken.None);

        result.Vector.Should().BeNull();
        handler.Requests.Should().BeEmpty("an unconfigured provider must not reach the network");
    }

    // --- The happy path ---

    [Fact]
    public async Task EmbedAsync_ShouldReturnTheVector_WhenCohereSucceeds()
    {
        StubHandler handler = new() { Body = EmbedResponse() };

        EmbeddingResult result = await Provider(handler).EmbedAsync("Fed holds rates", CancellationToken.None);

        result.Vector.Should().NotBeNull();
        result.Vector!.Should().HaveCount(1024);
        result.Outcome.Should().Be(EmbeddingOutcome.Embedded);
    }

    [Fact]
    public async Task EmbedAsync_ShouldSendTheKeyAsABearerToken_AndNeverInTheBody()
    {
        StubHandler handler = new() { Body = EmbedResponse() };

        await Provider(handler).EmbedAsync("text", CancellationToken.None);

        handler.LastRequest!.Headers.Authorization!.ToString().Should().Be($"Bearer {Key}");
        handler.LastBody.Should().NotContain(Key, "the key authenticates the request; it is never request content");
    }

    // --- Query vs document input type (gh#852: a retrieval query embeds as search_query, the write path as search_document) ---

    [Fact]
    public async Task EmbedQueryAsync_ShouldSendSearchQueryInputType_SoAQueryMatchesTheDocumentSide()
    {
        StubHandler handler = new() { Body = EmbedResponse() };

        await Provider(handler).EmbedQueryAsync("Fed decision", CancellationToken.None);

        handler.LastBody.Should().Contain(
            "\"input_type\":\"search_query\"", "a retrieval query must be embedded as search_query, not search_document");
    }

    [Fact]
    public async Task EmbedAsync_ShouldStillSendSearchDocumentInputType_AfterTheQuerySplit()
    {
        // Regression guard on the extract-a-shared-body refactor (gh#852): the write path's input type must not
        // change when EmbedQueryAsync is carved out beside it, or every stored vector silently shifts space.
        StubHandler handler = new() { Body = EmbedResponse() };

        await Provider(handler).EmbedAsync("Fed holds rates", CancellationToken.None);

        handler.LastBody.Should().Contain("\"input_type\":\"search_document\"");
    }

    [Fact]
    public async Task EmbedQueryAsync_ShouldReturnTheVector_WhenCohereSucceeds()
    {
        StubHandler handler = new() { Body = EmbedResponse() };

        EmbeddingResult result = await Provider(handler).EmbedQueryAsync("Fed decision", CancellationToken.None);

        result.Vector.Should().NotBeNull();
        result.Vector!.Should().HaveCount(1024);
        result.Outcome.Should().Be(EmbeddingOutcome.Embedded);
    }

    [Fact]
    public async Task EmbedQueryAsync_ShouldReturnNull_WhenRateLimited()
    {
        // The query path degrades identically to the document path — a 429 returns null, never throws into retrieval.
        StubHandler handler = new() { Status = HttpStatusCode.TooManyRequests };

        EmbeddingResult result = await Provider(handler).EmbedQueryAsync("query", CancellationToken.None);

        result.Vector.Should().BeNull("a 429 degrades retrieval to sparse — it must not throw into the caller");
        result.Outcome.Should().Be(EmbeddingOutcome.RateLimited);
    }

    // --- Fallback to sparse rather than throwing (the core safety property) ---

    [Fact]
    public async Task EmbedAsync_ShouldReturnNull_WhenRateLimited()
    {
        StubHandler handler = new() { Status = HttpStatusCode.TooManyRequests };

        EmbeddingResult result = await Provider(handler).EmbedAsync("text", CancellationToken.None);

        result.Vector.Should().BeNull("a 429 degrades retrieval to sparse — it must not throw into the caller");
        result.Outcome.Should().Be(EmbeddingOutcome.RateLimited);
    }

    [Fact]
    public async Task EmbedAsync_ShouldReturnNull_WhenCohereReturnsAServerError()
    {
        StubHandler handler = new() { Status = HttpStatusCode.InternalServerError };

        (await Provider(handler).EmbedAsync("text", CancellationToken.None)).Vector.Should().BeNull();
    }

    [Fact]
    public async Task EmbedAsync_ShouldReturnNull_WhenTheTransportThrows()
    {
        StubHandler handler = new() { Throw = new HttpRequestException("connection reset") };

        (await Provider(handler).EmbedAsync("text", CancellationToken.None)).Vector.Should().BeNull();
    }

    [Fact]
    public async Task EmbedAsync_ShouldReturnNull_WhenTheResponseBodyIsUnparseable()
    {
        StubHandler handler = new() { Body = "not json" };

        (await Provider(handler).EmbedAsync("text", CancellationToken.None)).Vector.Should().BeNull();
    }

    [Fact]
    public async Task EmbedAsync_ShouldRethrow_WhenTheCallerCancels()
    {
        StubHandler handler = new() { Throw = new OperationCanceledException() };
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Func<Task> act = () => Provider(handler).EmbedAsync("text", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a genuine caller cancellation is host shutdown, not a provider failure to swallow");
    }

    // --- Metering: every call, including a failover (gh#403: an unmetered call is invisible spend) ---

    [Fact]
    public async Task EmbedAsync_ShouldMeterTokensAndCost_WhenTheCallSucceeds()
    {
        StubHandler handler = new() { Body = EmbedResponse(inputTokens: 1_000_000) };

        EmbeddingResult result = await Provider(handler).EmbedAsync("text", CancellationToken.None);

        RecordedEmbed embed = _metrics.Records.Should().ContainSingle().Subject;
        embed.Outcome.Should().Be(EmbeddingOutcome.Embedded);
        embed.BilledTokens.Should().Be(1_000_000);
        embed.EstimatedCostUsd.Should().Be(0.10m, "one million tokens at the pinned $0.10/M rate");
        embed.Model.Should().Be("embed-english-v3.0");

        // The SAME facts also ride the return value (gh#377): the embed pass ledgers AIUsage from this, not from
        // the metrics sink, so the outcome/tokens/cost the caller sees must match what was metered exactly.
        result.Outcome.Should().Be(EmbeddingOutcome.Embedded);
        result.BilledTokens.Should().Be(1_000_000);
        result.EstimatedCostUsd.Should().Be(0.10m);
    }

    [Fact]
    public async Task EmbedAsync_ShouldMeterAFailover_AsRateLimitedWithZeroCost()
    {
        StubHandler handler = new() { Status = HttpStatusCode.TooManyRequests };

        EmbeddingResult result = await Provider(handler).EmbedAsync("text", CancellationToken.None);

        RecordedEmbed embed = _metrics.Records.Should().ContainSingle(
            "a call that failed over is still a call — leaving it unmetered would hide the degrade").Subject;
        embed.Outcome.Should().Be(EmbeddingOutcome.RateLimited);
        embed.BilledTokens.Should().Be(0);
        embed.EstimatedCostUsd.Should().Be(0m);
        embed.Latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);

        result.Outcome.Should().Be(EmbeddingOutcome.RateLimited);
        result.BilledTokens.Should().Be(0);
        result.EstimatedCostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task EmbedAsync_ShouldMeterATransportFailure_AsFailed()
    {
        StubHandler handler = new() { Throw = new HttpRequestException("boom") };

        await Provider(handler).EmbedAsync("text", CancellationToken.None);

        _metrics.Records.Should().ContainSingle().Which.Outcome.Should().Be(EmbeddingOutcome.Failed);
    }

    // --- Doubles ---

    private sealed record RecordedEmbed(
        string Model, EmbeddingOutcome Outcome, int BilledTokens, decimal EstimatedCostUsd, TimeSpan Latency);

    private sealed class RecordingEmbeddingMetrics : IEmbeddingMetrics
    {
        public List<RecordedEmbed> Records { get; } = [];

        public void RecordEmbed(string model, EmbeddingOutcome outcome, int billedTokens, decimal estimatedCostUsd, TimeSpan latency) =>
            Records.Add(new RecordedEmbed(model, outcome, billedTokens, estimatedCostUsd, latency));
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
