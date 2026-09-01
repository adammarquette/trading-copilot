using System.Diagnostics.Metrics;
using System.Net;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.IntegrationTests.Ai;

/// <summary>
/// Independent QA for gh#976 (of gh#975, ADR-0008) — the Cohere rerank provider's <b>live contract</b> and its
/// <b>keyless degrade</b>, mirroring the embedding provider's own unit/integration split (gh#403). The adapter's
/// exhaustive fault-degrade / metering-value matrix (429, 5xx, malformed body, transport throw, top-N truncation,
/// out-of-range indices) is already unit-covered against a fake <see cref="HttpMessageHandler"/>
/// (<c>CohereRerankProviderTests</c>) — this suite proves what only <b>real DI composition</b>, a <b>real network
/// round trip</b>, and a <b>real OpenTelemetry meter</b> can: the keyless default really is what production's own
/// switch resolves and it never reaches the network however it is driven; a genuine socket round trip to a
/// Cohere-shaped endpoint parses into a valid permutation ordered by descending relevance; and a real (stubbed)
/// call — success or fault — increments the real <c>ai.rerank.*</c> instruments on the shared
/// <c>MarqSpec.TradingCopilot.Ai</c> meter, not a fake recorder.
/// </summary>
/// <remarks>
/// <para>
/// <b>No Cohere credentials exist in this environment</b> (the rerank card is operator-gated; keyless-degrade is
/// half its point). <see cref="RerankAsync_ShouldSatisfyTheLiveContract_AgainstTheRealCohereEndpoint"/> is gated by
/// <see cref="CohereLiveFactAttribute"/> and therefore <b>skips</b> here and in this repository's current pre-merge
/// CI leg — an honest, reported skip rather than a fabricated pass. The other three cases need no external
/// credential at all: the keyless-degrade case proves production's own DI switch; the wire-shape and fault cases
/// drive the real <see cref="CohereRerankProvider"/> against <see cref="StubCohereRerankServer"/>, a genuine local
/// Kestrel listener on loopback — a real socket round trip, never a fake transport — standing in only for the far
/// end (Cohere itself), exactly as the QA "nothing mocked" discipline permits for an outbound third-party seam
/// that cannot exist pre-merge.
/// </para>
/// <para>
/// <b>Meter isolation.</b> <see cref="RerankMetrics"/>'s own constructor takes an optional meter-name override
/// "so its <c>MeterListener</c> observes only its own instance, never one from a class running in parallel" (its
/// own doc comment) — the same isolation <c>AlertRuleSeriesReconciliationTests</c> uses. The wire-shape and fault
/// cases below construct <see cref="CohereRerankProvider"/> directly over a uniquely-named <see cref="RerankMetrics"/>
/// for exactly that reason, rather than resolving it from a shared DI graph other parallel suites might also touch.
/// </para>
/// </remarks>
public sealed class CohereRerankLiveContractIntegrationTests
{
    private static IReadOnlyList<string> Docs(int count) => [.. Enumerable.Range(0, count).Select(i => $"doc-{i}")];

    // =================================================================================================================
    // AC2 — keyless degrade is real, by construction: production's OWN DI switch (AiRegistration.AddTradingCopilotAi),
    // never a hand-built UnavailableReranker, resolves the passthrough and never touches the network.
    // =================================================================================================================

    [Fact]
    public async Task RerankAsync_ShouldResolveTheKeylessDefault_AndNeverTouchTheNetwork_WhenNoCohereKeyIsConfigured()
    {
        ThrowingHandler handler = new();
        ServiceCollection services = new();
        services.AddLogging();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        // The SAME plain registration Program.cs makes (no Cohere:ApiKey above), plus a wrapping handler that
        // THROWS on any send -- wrapping the real named client, never replacing IReranker's own registration, so a
        // regression that made the keyless default reach the network would be caught, not silently bypassed.
        services.AddHttpClient(CohereEmbeddingProvider.HttpClientName).AddHttpMessageHandler(() => handler);
        services.AddTradingCopilotAi(config);

        await using ServiceProvider provider = services.BuildServiceProvider();
        IReranker reranker = provider.GetRequiredService<IReranker>();

        reranker.Should().BeOfType<UnavailableReranker>(
            "gh#975: with no Cohere key configured, production's OWN switch — not a test double — must resolve the keyless passthrough");
        reranker.IsAvailable.Should().BeFalse("an ordinary, expected state — no key configured");

        RerankResult result = await reranker.RerankAsync("q", Docs(3), topN: 3, CancellationToken.None);

        result.Ranking.Select(r => r.Index).Should().Equal([0, 1, 2], "the keyless default degrades to identity (passthrough) order");
        handler.Requests.Should().BeEmpty("the keyless default must never reach the network, however it is driven through real DI");
    }

    // =================================================================================================================
    // AC1 — the live contract's WIRE SHAPE holds: a real socket round trip parses into a permutation, descending
    // relevance, an obviously-relevant candidate outranking an obviously-irrelevant one.
    // =================================================================================================================

    [Fact]
    public async Task RerankAsync_ShouldRoundTripARealSocket_AndParseAPermutationOrderedByDescendingRelevance()
    {
        await using StubCohereRerankServer stub = await StubCohereRerankServer.StartAsync();
        // Index 3 ("the Fed just cut rates") is scripted as the clear winner; index 2 ("obviously irrelevant
        // weather report") as the clear loser -- so the "obviously relevant outranks obviously irrelevant"
        // assertion below is checking a real, deliberately unambiguous label, not an arbitrary index.
        stub.Script(HttpStatusCode.OK, RankedResponse(searchUnits: 1, (3, 0.93), (1, 0.51), (0, 0.30), (2, 0.02)));

        using RerankMetrics metrics = new("test.rerank.wire." + Guid.NewGuid().ToString("N"));
        CohereRerankProvider provider = new(
            new RealHttpClientFactory(),
            Options.Create(new CohereOptions { ApiKey = "integration-test-key-not-a-secret", BaseUrl = stub.BaseUrl }),
            metrics,
            NullLogger<CohereRerankProvider>.Instance);

        RerankResult result = await provider.RerankAsync(
            "Fed decision",
            ["irrelevant chatter about lunch", "somewhat related market chatter", "obviously irrelevant weather report", "the Fed just cut rates"],
            topN: 4,
            CancellationToken.None);

        // Real wire shape: the stub really received one POST, with the real Bearer header and the real JSON body
        // CohereRerankProvider serializes -- proven by inspecting what actually crossed the socket, not a mock's
        // recorded call.
        StubCohereRerankServer.RecordedRequest request = stub.Requests.Should().ContainSingle().Which;
        request.Authorization.Should().Be("Bearer integration-test-key-not-a-secret");
        request.Body.Should().Contain("\"query\":\"Fed decision\"").And.Contain("\"top_n\":4");

        result.Outcome.Should().Be(RerankOutcome.Reranked);
        result.Ranking.Select(r => r.Index).OrderBy(i => i).Should().Equal(
            [0, 1, 2, 3], "a real rerank result is a PERMUTATION of the candidate set — every index present, none duplicated");
        result.Ranking.Select(r => r.RelevanceScore).Should().BeInDescendingOrder("relevance is ranked, most relevant first");
        result.Ranking[0].Index.Should().Be(3, "the obviously-relevant candidate ('the Fed just cut rates') must outrank the obviously-irrelevant ones");
    }

    // =================================================================================================================
    // AC3 / AC4 — a genuine wire fault degrades to passthrough AND increments the REAL shared meter as a failure,
    // not a fake recorder's tally.
    // =================================================================================================================

    [Fact]
    public async Task RerankAsync_ShouldDegradeToPassthrough_AndIncrementTheRealMeterAsFailed_OnAGenuineWireFault()
    {
        await using StubCohereRerankServer stub = await StubCohereRerankServer.StartAsync();
        stub.Script(HttpStatusCode.ServiceUnavailable, "");

        string meterName = "test.rerank.fault." + Guid.NewGuid().ToString("N");
        using RerankMetrics metrics = new(meterName);
        CohereRerankProvider provider = new(
            new RealHttpClientFactory(),
            Options.Create(new CohereOptions { ApiKey = "integration-test-key-not-a-secret", BaseUrl = stub.BaseUrl }),
            metrics,
            NullLogger<CohereRerankProvider>.Instance);

        List<(string Outcome, long Calls)> published = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == meterName && instrument.Name == RerankMetrics.RerankCalls)
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string? outcome = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcome = tag.Value?.ToString();
                }
            }

            published.Add((outcome ?? "", measurement));
        });
        listener.Start();

        RerankResult result = await provider.RerankAsync("q", Docs(3), topN: 3, CancellationToken.None);

        result.Ranking.Select(r => r.Index).Should().Equal([0, 1, 2], "a genuine 503 over the wire degrades to identity order, never a throw");
        result.Outcome.Should().Be(RerankOutcome.Failed);

        published.Should().ContainSingle(
            call => call.Outcome == "failed" && call.Calls == 1,
            "the REAL ai.rerank.calls instrument on the shared meter must record this fault — a call that degraded is still a call");
    }

    // =================================================================================================================
    // AC1 (gated) — the real /v2/rerank contract, when a live credential is present. Skips gracefully otherwise.
    // =================================================================================================================

    [CohereLiveFact]
    public async Task RerankAsync_ShouldSatisfyTheLiveContract_AgainstTheRealCohereEndpoint()
    {
        string key = Environment.GetEnvironmentVariable(CohereLiveFactAttribute.KeyEnvironmentVariable)!;
        using RerankMetrics metrics = new("test.rerank.live." + Guid.NewGuid().ToString("N"));
        CohereRerankProvider provider = new(
            new RealHttpClientFactory(),
            Options.Create(new CohereOptions { ApiKey = key }),
            metrics,
            NullLogger<CohereRerankProvider>.Instance);

        RerankResult result = await provider.RerankAsync(
            "What is the capital of France?",
            ["Paris is the capital and largest city of France.", "Bananas are a good source of potassium."],
            topN: 2,
            CancellationToken.None);

        result.Outcome.Should().Be(RerankOutcome.Reranked);
        result.Ranking.Select(r => r.Index).OrderBy(i => i).Should().Equal([0, 1]);
        result.Ranking.Select(r => r.RelevanceScore).Should().OnlyContain(score => score >= 0d && score <= 1d);
        result.Ranking.Select(r => r.RelevanceScore).Should().BeInDescendingOrder();
        result.Ranking[0].Index.Should().Be(0, "the obviously-relevant candidate must outrank the obviously-irrelevant one against the REAL model");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    private static string RankedResponse(int searchUnits, params (int Index, double Score)[] results)
    {
        string ranked = string.Join(",", results.Select(r =>
            $"{{\"index\":{r.Index},\"relevance_score\":{r.Score.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"));
        return "{\"results\":[" + ranked + "],\"meta\":{\"billed_units\":{\"search_units\":" + searchUnits + "}}}";
    }

    /// <summary>A REAL <see cref="IHttpClientFactory"/> -- production's own plain <c>AddHttpClient(name)</c> shape, no BaseAddress.</summary>
    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>A handler that throws on ANY send -- proves the keyless default never reaches the network, by construction.</summary>
    private sealed class ThrowingHandler : DelegatingHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            throw new InvalidOperationException("The keyless rerank default must never reach the network.");
        }
    }
}
