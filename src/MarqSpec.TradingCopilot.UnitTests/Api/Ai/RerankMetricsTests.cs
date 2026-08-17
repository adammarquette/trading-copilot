using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The rerank spend meter (gh#975). The property it guards: a call is recorded <b>whatever its outcome</b>, so a
/// degrade is visible spend-wise (as a call at zero cost) rather than an absence indistinguishable from a quiet
/// period — the gh#403 embed-meter discipline, extended to rerank. It rides the same
/// <see cref="EmbeddingMetrics.MeterName"/> meter, with distinct <c>ai.rerank.*</c> instrument names.
/// </summary>
public sealed class RerankMetricsTests : IDisposable
{
    // A unique meter per test instance: xUnit runs classes in parallel, and a listener filtering on the shared meter
    // NAME would receive another instance's measurements mid-enumeration.
    private readonly string _meterName = EmbeddingMetrics.MeterName + ".RerankTest." + Guid.NewGuid().ToString("N");
    private readonly RerankMetrics _metrics;
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, double Value, Dictionary<string, string?> Tags)> _measurements = [];

    public RerankMetricsTests()
    {
        _metrics = new RerankMetrics(_meterName);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == _meterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Add((instrument.Name, value, TagsOf(tags))));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _measurements.Add((instrument.Name, value, TagsOf(tags))));
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public void RecordRerank_ShouldEmitSearchesCostAndLatency_TaggedByModelAndOutcome()
    {
        _metrics.RecordRerank("rerank-english-v3.0", RerankOutcome.Reranked, billedSearches: 1, estimatedCostUsd: 0.002m, latency: TimeSpan.FromMilliseconds(42));

        Measurement(RerankMetrics.RerankSearches).Value.Should().Be(1);
        Measurement(RerankMetrics.RerankCost).Value.Should().BeApproximately(0.002, 1e-9);
        Measurement(RerankMetrics.RerankLatency).Value.Should().BeApproximately(42, 1e-9);
        Measurement(RerankMetrics.RerankCalls).Tags["model"].Should().Be("rerank-english-v3.0");
        Measurement(RerankMetrics.RerankCalls).Tags["outcome"].Should().Be("reranked");
    }

    [Fact]
    public void RecordRerank_ShouldStillCountACall_WhenTheOutcomeIsADegradeAtZeroCost()
    {
        _metrics.RecordRerank("rerank-english-v3.0", RerankOutcome.RateLimited, billedSearches: 0, estimatedCostUsd: 0m, latency: TimeSpan.FromMilliseconds(9));

        // The call and its latency are the evidence a degrade happened; without them a passthrough is invisible.
        Measurement(RerankMetrics.RerankCalls).Value.Should().Be(1);
        Measurement(RerankMetrics.RerankCalls).Tags["outcome"].Should().Be("ratelimited");
        Measurement(RerankMetrics.RerankLatency).Value.Should().BeApproximately(9, 1e-9);
    }

    [Fact]
    public void Cost_ShouldDeclareAnAnnotationUnit_SoTheExporterDoesNotAppendItToTheName()
    {
        // The gh#505 lesson, inherited: prometheusremotewrite appends a non-mapped unit like "USD" verbatim to the
        // series name, so `ai_rerank_cost_usd_total` would silently return nothing while `ai_rerank_cost_usd_USD_total`
        // held the data — on a cost meter, indistinguishable from "no spend". The braces annotation form is not appended.
        string? unit = UnitOf(RerankMetrics.RerankCost);

        unit.Should().StartWith("{", "an annotation unit is not appended to the exported series name")
            .And.EndWith("}");
    }

    [Fact]
    public void NullRerankMetrics_ShouldRecordNothing_WithoutThrowing()
    {
        // The no-op sink an unconfigured deployment gets: it must accept a call silently, never throw.
        Action record = () => NullRerankMetrics.Instance.RecordRerank(
            "none", RerankOutcome.Failed, billedSearches: 0, estimatedCostUsd: 0m, latency: TimeSpan.Zero);

        record.Should().NotThrow();
    }

    private (double Value, Dictionary<string, string?> Tags) Measurement(string instrument)
    {
        (string Instrument, double Value, Dictionary<string, string?> Tags) match =
            _measurements.Single(m => m.Instrument == instrument);
        return (match.Value, match.Tags);
    }

    /// <summary>The declared unit of an instrument on this test's meter — read from the published instrument.</summary>
    private string? UnitOf(string instrumentName)
    {
        string? unit = null;
        using MeterListener probe = new();
        probe.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == _meterName && instrument.Name == instrumentName)
            {
                unit = instrument.Unit;
            }
        };
        probe.Start();
        return unit;
    }

    private static Dictionary<string, string?> TagsOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string?> map = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString();
        }

        return map;
    }
}
