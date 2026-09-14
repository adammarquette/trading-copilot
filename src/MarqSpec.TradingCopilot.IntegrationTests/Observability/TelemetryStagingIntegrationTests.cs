using System.Globalization;
using System.Text.Json;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;
using Xunit;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// <b>Post-merge staging tier (gh#332 ⇒ gh#231, ADR-0002, engineering §7, §10).</b> The claim under test is that the
/// system is <b>observable</b>, not merely instrumented: the pre-merge suites (gh#329, gh#330, gh#331) prove the
/// signals are emitted, correlated and bounded <i>in process</i>; only this one proves they <b>arrive and can be
/// queried</b>. A metric nobody can select, or a trace that never lands in Tempo, satisfies a checklist without
/// producing visibility.
/// </summary>
/// <remarks>
/// <para>
/// Venue-independent but <b>backend-dependent</b>, so these SKIP in pre-merge CI and run only where
/// <c>STAGING_PROMETHEUS_URL</c> / <c>STAGING_LOKI_URL</c> / <c>STAGING_TEMPO_URL</c> are set — endpoints from CI
/// secrets, never from source.
/// </para>
/// <para>
/// <b>Names are matched by family, not pinned exactly.</b> The OTLP → Prometheus path decorates by instrument kind
/// (<c>_total</c> on a counter, <c>_bucket</c> / <c>_sum</c> / <c>_count</c> on a histogram, a gauge bare), so exact
/// strings would encode one exporter's convention and break on a collector upgrade that is no regression at all.
/// <see cref="PrometheusMetricNames"/> owns that rule and is <b>unit-tested in the pre-merge tier</b> — deliberately,
/// because the matching is the part most likely to be wrong, and a rule that silently never matches would make this
/// suite pass vacuously.
/// </para>
/// <para>
/// <b>Verification status — stated plainly.</b> This file is structurally complete and skips cleanly without the
/// environment, but its assertions have <b>not been run green against a live stack</b>: that first run happens
/// post-merge to <c>staging</c>, and it is where the Loki label value and Tempo's search shape are confirmed. An
/// unrun staging guard is a guard whose first execution is its own proving, and it should be read that way until
/// then. What <i>could</i> be proven here — the name matching — was extracted precisely so it could be.
/// </para>
/// </remarks>
[Trait("Category", "Staging")]
public sealed class TelemetryStagingIntegrationTests
{
    /// <summary>The resource attribute gh#230 stamps on every signal — the service selector for Loki and Tempo.</summary>
    private const string ServiceName = "trading-copilot-api";

    /// <summary>A closed dimension set cannot produce many series; a few hundred means an id reached a label.</summary>
    private const double SeriesCeilingPerFamily = 500;

    [StagingObservabilityFact]
    public async Task Prometheus_ShouldServeEveryExecutionSli_AfterTheSystemHasRun()
    {
        // Not "some metric exists" — EVERY execution SLI gh#232 / gh#295 defines must be selectable, or a dashboard
        // panel built on the missing one renders nothing and the gap reads as a quiet day rather than a hole.
        using HttpClient prometheus = new() { BaseAddress = new Uri(StagingConfig.PrometheusBaseUrl!) };

        using HttpResponseMessage response = await prometheus.GetAsync("/api/v1/label/__name__/values");
        response.EnsureSuccessStatusCode();

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("success", "Prometheus answered the name query");

        List<string> scraped =
        [
            .. body.RootElement.GetProperty("data").EnumerateArray()
                .Select(name => name.GetString())
                .Where(name => name is not null)
                .Select(name => name!),
        ];

        PrometheusMetricNames.MissingExecutionSlis(scraped).Should().BeEmpty(
            "every execution SLI must be scraped and selectable — one that is only emitted is not observable");
    }

    [StagingObservabilityFact]
    public async Task Prometheus_ShouldNotExplodeTheSeriesCount_ForTheExecutionSlis()
    {
        // The cardinality bound against a real TSDB rather than a listener: the pre-merge guard (gh#330) proves no
        // unbounded value is USED as a label; this proves the resulting series count stays small in practice. An
        // unbounded label set takes the backend down — instrumentation causing the outage it exists to reveal (§9).
        using HttpClient prometheus = new() { BaseAddress = new Uri(StagingConfig.PrometheusBaseUrl!) };

        using HttpResponseMessage response = await prometheus.GetAsync(
            "/api/v1/query?query=" + Uri.EscapeDataString("count({__name__=~\"trading_.*\"}) by (__name__)"));
        response.EnsureSuccessStatusCode();

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (JsonElement family in body.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
        {
            double series = double.Parse(family.GetProperty("value")[1].GetString()!, CultureInfo.InvariantCulture);
            series.Should().BeLessThan(
                SeriesCeilingPerFamily,
                "the execution SLIs carry a closed dimension set, so {0} stays a handful of series",
                family.GetProperty("metric").GetProperty("__name__").GetString());
        }
    }

    [StagingObservabilityFact]
    public async Task Loki_ShouldServeLogsFilterableByTraceId()
    {
        // The trace → logs half of the cross-pillar pivot (ADR-0002). Filterability by trace id is the property: a
        // log stream that arrives without it cannot be jumped to from a trace, which is the entire point of the
        // pivot. STAGING first-run: confirm the label name Loki exposes for it.
        using HttpClient loki = new() { BaseAddress = new Uri(StagingConfig.LokiBaseUrl!) };

        using HttpResponseMessage response = await loki.GetAsync(
            "/loki/api/v1/query_range?limit=5&query="
            + Uri.EscapeDataString($"{{service_name=\"{ServiceName}\"}} | trace_id != \"\""));
        response.EnsureSuccessStatusCode();

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("success", "Loki answered the trace-filtered query");
        body.RootElement.GetProperty("data").GetProperty("result").GetArrayLength().Should().BeGreaterThan(
            0, "logs emitted inside a span carry their trace id, so the trace → logs pivot resolves");
    }

    [StagingObservabilityFact]
    public async Task Tempo_ShouldReturnTheApiTraces_WhenSearched()
    {
        // Traces must be RETRIEVABLE, not merely exported. Searched by service rather than a hard-coded trace id: a
        // fixed id would rot the moment ADR-0002's 7-day retention rolled past it, and the test would then fail for
        // a reason that is not a regression.
        using HttpClient tempo = new() { BaseAddress = new Uri(StagingConfig.TempoBaseUrl!) };

        using HttpResponseMessage response = await tempo.GetAsync(
            "/api/search?limit=1&tags=" + Uri.EscapeDataString($"service.name={ServiceName}"));
        response.EnsureSuccessStatusCode();

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("traces", out JsonElement traces).Should().BeTrue(
            "Tempo answered a search for the API's traces");
        traces.GetArrayLength().Should().BeGreaterThan(
            0, "the API's traces reach Tempo and are searchable — exported, stored and queryable, not just emitted");
    }
}
