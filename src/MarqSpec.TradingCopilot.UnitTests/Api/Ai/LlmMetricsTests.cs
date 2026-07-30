using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The agent-review LLM-spend meter (gh#477). The property it guards: a call is metered <b>whatever its outcome</b> —
/// a failed / refused / truncated call records as a call at zero tokens and zero cost, so a fault is visible spend-wise
/// rather than an absence indistinguishable from a quiet period (the gh#403 embed-meter discipline, extended to LLM).
/// Dimensioned by model, tier and outcome only — the reviewed text is never a tag.
/// </summary>
public sealed class LlmMetricsTests : IDisposable
{
    // A unique meter per test instance: xUnit runs classes in parallel, and a listener filtering on the shared meter
    // NAME would receive another instance's measurements mid-enumeration.
    private readonly string _meterName = EmbeddingMetrics.MeterName + ".LlmTest." + Guid.NewGuid().ToString("N");
    private readonly LlmMetrics _metrics;
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, double Value, Dictionary<string, string?> Tags)> _measurements = [];

    public LlmMetricsTests()
    {
        _metrics = new LlmMetrics(_meterName);
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

    private static AiCallCost Cost(
        string model = "claude-sonnet-5",
        LlmModelTier? tier = LlmModelTier.Deep,
        AiUsageOutcome outcome = AiUsageOutcome.Succeeded,
        int inputTokens = 1200,
        int outputTokens = 300,
        decimal costUsd = 0.02m,
        double latencyMs = 800) =>
        new(AiUsageFeature.Triage, model, tier, outcome, inputTokens, outputTokens, costUsd, TimeSpan.FromMilliseconds(latencyMs));

    [Fact]
    public void RecordLlmCall_ShouldEmitCallTokensCostAndLatency_TaggedByModelTierAndOutcome()
    {
        _metrics.RecordLlmCall(Cost(
            model: "claude-sonnet-5", tier: LlmModelTier.Deep, outcome: AiUsageOutcome.Succeeded,
            inputTokens: 1200, outputTokens: 300, costUsd: 0.0195m, latencyMs: 800));

        Measurement(LlmMetrics.LlmCalls).Value.Should().Be(1);
        Measurement(LlmMetrics.LlmInputTokens).Value.Should().Be(1200);
        Measurement(LlmMetrics.LlmOutputTokens).Value.Should().Be(300);
        Measurement(LlmMetrics.LlmCost).Value.Should().BeApproximately(0.0195, 1e-9);
        Measurement(LlmMetrics.LlmLatency).Value.Should().BeApproximately(800, 1e-9);

        Dictionary<string, string?> tags = Measurement(LlmMetrics.LlmCalls).Tags;
        tags["model"].Should().Be("claude-sonnet-5");
        tags["tier"].Should().Be("deep");
        tags["outcome"].Should().Be("succeeded");
    }

    [Fact]
    public void RecordLlmCall_ShouldTagTheTriageTier_OnATriageCall()
    {
        _metrics.RecordLlmCall(Cost(model: "claude-haiku-4-5", tier: LlmModelTier.Triage));

        Measurement(LlmMetrics.LlmCalls).Tags["tier"].Should().Be("triage");
    }

    [Fact]
    public void RecordLlmCall_ShouldStillCountACall_WhenTheOutcomeIsAFailedZeroTokenCall()
    {
        // A provider fault is billable latency that produced nothing (gh#431): the meter must show it as a call at zero
        // cost, not an absence — the exact failure regime a success-only meter would silently miss.
        _metrics.RecordLlmCall(Cost(
            outcome: AiUsageOutcome.Failed, inputTokens: 0, outputTokens: 0, costUsd: 0m, latencyMs: 15));

        Measurement(LlmMetrics.LlmCalls).Value.Should().Be(1);
        Measurement(LlmMetrics.LlmCalls).Tags["outcome"].Should().Be("failed");
        Measurement(LlmMetrics.LlmInputTokens).Value.Should().Be(0);
        Measurement(LlmMetrics.LlmOutputTokens).Value.Should().Be(0);
        Measurement(LlmMetrics.LlmCost).Value.Should().BeApproximately(0, 1e-9);
        Measurement(LlmMetrics.LlmLatency).Value.Should().BeApproximately(15, 1e-9);
    }

    private (double Value, Dictionary<string, string?> Tags) Measurement(string instrument)
    {
        (string Instrument, double Value, Dictionary<string, string?> Tags) match =
            _measurements.Single(m => m.Instrument == instrument);
        return (match.Value, match.Tags);
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
