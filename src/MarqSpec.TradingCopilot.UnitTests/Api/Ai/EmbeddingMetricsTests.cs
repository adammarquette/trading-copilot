using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The embedding spend meter (gh#403). The property it guards: a call is recorded <b>whatever its outcome</b>, so
/// a failover is visible spend-wise (as a call at zero cost) rather than an absence indistinguishable from a
/// quiet period — the same absence-detection discipline the execution SLIs hold.
/// </summary>
public sealed class EmbeddingMetricsTests : IDisposable
{
    // A unique meter per test instance: xUnit runs classes in parallel, and a listener filtering on the shared
    // meter NAME would receive another instance's measurements mid-enumeration.
    private readonly string _meterName = EmbeddingMetrics.MeterName + ".Test." + Guid.NewGuid().ToString("N");
    private readonly EmbeddingMetrics _metrics;
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, double Value, Dictionary<string, string?> Tags)> _measurements = [];

    public EmbeddingMetricsTests()
    {
        _metrics = new EmbeddingMetrics(_meterName);
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
    public void RecordEmbed_ShouldEmitTokensCostAndLatency_TaggedByModelAndOutcome()
    {
        _metrics.RecordEmbed("embed-english-v3.0", EmbeddingOutcome.Embedded, billedTokens: 1_000_000, estimatedCostUsd: 0.10m, latency: TimeSpan.FromMilliseconds(42));

        Measurement(EmbeddingMetrics.EmbedTokens).Value.Should().Be(1_000_000);
        Measurement(EmbeddingMetrics.EmbedCost).Value.Should().BeApproximately(0.10, 1e-9);
        Measurement(EmbeddingMetrics.EmbedLatency).Value.Should().BeApproximately(42, 1e-9);
        Measurement(EmbeddingMetrics.EmbedCalls).Tags["model"].Should().Be("embed-english-v3.0");
        Measurement(EmbeddingMetrics.EmbedCalls).Tags["outcome"].Should().Be("embedded");
    }

    [Fact]
    public void RecordEmbed_ShouldStillCountACall_WhenTheOutcomeIsAFailoverAtZeroCost()
    {
        _metrics.RecordEmbed("embed-english-v3.0", EmbeddingOutcome.RateLimited, billedTokens: 0, estimatedCostUsd: 0m, latency: TimeSpan.FromMilliseconds(9));

        // The call and its latency are the evidence a degrade happened; without them a failover is invisible.
        Measurement(EmbeddingMetrics.EmbedCalls).Value.Should().Be(1);
        Measurement(EmbeddingMetrics.EmbedCalls).Tags["outcome"].Should().Be("ratelimited");
        Measurement(EmbeddingMetrics.EmbedLatency).Value.Should().BeApproximately(9, 1e-9);
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
