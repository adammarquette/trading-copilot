using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;
using Xunit;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// <b>DRAFT — post-merge staging tier (gh#234 ⇒ gh#230/#231/#232, ADR-0002, engineering §7).</b> Asserts the system
/// is actually observable, against the <b>live</b> Prometheus / Loki / Tempo backends (QA contract §4). These are
/// <see cref="StagingObservabilityFactAttribute">venue-independent but backend-dependent</see>, so they SKIP in
/// pre-merge CI and run only where <c>STAGING_PROMETHEUS_URL</c> / <c>STAGING_LOKI_URL</c> / <c>STAGING_TEMPO_URL</c>
/// are set. The exact metric / label / span names come from gh#232's instrumentation and are <b>pinned on the first
/// staging run</b> — the assertions below are the shape; the string constants are the seam to confirm there.
/// </summary>
/// <remarks>
/// The issue also carves out a pre-merge portion — that instrumentation <i>emits</i>, that a span link is <i>formed</i>,
/// that cardinality is <i>bounded</i> — which belongs in an in-process collector/exporter double, not here. This file
/// is only the backend-querying half; the split is deliberate (a guard that only runs post-merge is a guard that
/// mostly does not run).
/// </remarks>
[Trait("Category", "Staging")]
public sealed class TelemetryStagingIntegrationTests
{
    // STAGING first-run: replace with the real SLI metric family from gh#232 (e.g. execution latency / gate outcome).
    private const string ExecutionSliMetric = "trading_execution_orders_total";

    [StagingObservabilityFact]
    public async Task Telemetry_ShouldExposeExecutionSlis_OnPrometheus()
    {
        using HttpClient prometheus = new() { BaseAddress = new Uri(StagingConfig.PrometheusBaseUrl!) };

        using HttpResponseMessage response = await prometheus.GetAsync(
            $"/api/v1/query?query={Uri.EscapeDataString(ExecutionSliMetric)}");
        response.EnsureSuccessStatusCode();

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        root.GetProperty("status").GetString().Should().Be("success", "Prometheus answered the SLI query");
        root.GetProperty("data").GetProperty("result").GetArrayLength()
            .Should().BeGreaterThan(0, "the execution SLI metric is scraped and present — the system exports it, not just intends to");
    }

    [StagingObservabilityFact]
    public async Task Telemetry_ShouldRetainDecisionLogs_InLoki()
    {
        using HttpClient loki = new() { BaseAddress = new Uri(StagingConfig.LokiBaseUrl!) };

        // STAGING first-run: confirm the service label value from the gh#230 resource attributes.
        using HttpResponseMessage response = await loki.GetAsync(
            "/loki/api/v1/query_range?query=" + Uri.EscapeDataString("{service_name=\"trading-copilot-api\"}") + "&limit=1");
        response.EnsureSuccessStatusCode();

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString()
            .Should().Be("success", "Loki is ingesting the API's structured logs");
    }

    [StagingObservabilityFact]
    public async Task Telemetry_ShouldLinkSpansAcrossTheEventBackbone_InTempo()
    {
        using HttpClient tempo = new() { BaseAddress = new Uri(StagingConfig.TempoBaseUrl!) };

        // STAGING first-run: assert a producer→consumer span LINK across the event log (ADR-0002) once a known
        // trace_id is available — the defining "distributed, not just logged" property. Reachability is the floor.
        using HttpResponseMessage response = await tempo.GetAsync("/api/echo");
        ((int)response.StatusCode).Should().BeLessThan(500, "Tempo is reachable for trace-link assertions");
    }
}
