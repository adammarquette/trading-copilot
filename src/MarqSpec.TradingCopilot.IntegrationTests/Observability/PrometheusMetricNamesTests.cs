using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// The instrument → Prometheus name matching the staging telemetry suite depends on (gh#332). Pure, so it runs in
/// the <b>pre-merge</b> tier: the staging assertions themselves cannot, and this is the part most likely to be wrong
/// — a matching rule that silently never matches would make the staging suite pass vacuously, or fail on the first
/// staging run for a reason that has nothing to do with the system.
/// </summary>
public class PrometheusMetricNamesTests
{
    [Fact]
    public void ToBaseName_ShouldNormaliseDotsToUnderscores()
    {
        PrometheusMetricNames.ToBaseName(ExecutionMetrics.GateDecisions).Should().Be("trading_gate_decisions");
        PrometheusMetricNames.ToBaseName(ExecutionMetrics.PipelineLag).Should().Be("trading_eventlog_pipeline_lag");
    }

    [Fact]
    public void MissingExecutionSlis_ShouldReportNone_WhenEveryFamilyIsScrapedWithItsExporterSuffix()
    {
        // The real shape of a Prometheus name list: counters carry _total, histograms fan out into
        // _bucket/_sum/_count, gauges are bare. All eight families are present here.
        string[] scraped =
        [
            "trading_gate_decisions_total",
            "trading_flatten_deadlines_total",
            "trading_flatten_time_to_flat_bucket",
            "trading_flatten_time_to_flat_sum",
            "trading_order_ack_latency_bucket",
            "trading_killswitch_engaged",
            "trading_stops_orphaned",
            "trading_eventlog_retention_gaps_total",
            "trading_eventlog_pipeline_lag_bucket",
            "up", // an unrelated scrape target, as any real list has
        ];

        PrometheusMetricNames.MissingExecutionSlis(scraped).Should().BeEmpty();
    }

    [Fact]
    public void MissingExecutionSlis_ShouldNameTheFamily_WhenItIsNotScraped()
    {
        // The guard that matters: a family that stopped being exported must be REPORTED, not tolerated.
        string[] scraped = ["trading_gate_decisions_total", "trading_killswitch_engaged"];

        PrometheusMetricNames.MissingExecutionSlis(scraped)
            .Should().Contain(ExecutionMetrics.OrphanedStops)
            .And.Contain(ExecutionMetrics.OrderAckLatency)
            .And.NotContain(ExecutionMetrics.GateDecisions);
    }

    [Fact]
    public void MissingExecutionSlis_ShouldNotMatchAMerelySimilarName()
    {
        // A prefix rule that matched anything starting with the base would accept a DIFFERENT metric and report a
        // family as served when it is not — the vacuous pass this helper exists to prevent. Only an exact match or
        // an underscore-delimited suffix counts.
        string[] scraped = ["trading_stops_orphanedxyz", "trading_gate_decisionsomething"];

        PrometheusMetricNames.MissingExecutionSlis(scraped)
            .Should().Contain(ExecutionMetrics.OrphanedStops, "a name that merely starts with the base is not that family")
            .And.Contain(ExecutionMetrics.GateDecisions);
    }
}
