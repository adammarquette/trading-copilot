using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The AI-spend meter for embeddings (gh#403, ADR-0002, engineering §AI-usage-and-spend): tokens, estimated
/// cost and latency per call, dimensioned by model and outcome, exported to Prometheus and aggregated in Grafana
/// as the visibility behind the ADR-0008 spend governor.
/// </summary>
/// <remarks>
/// The dimension set is closed on purpose — <c>model</c> and <c>outcome</c>, both low-cardinality — so the
/// series count cannot explode against a real TSDB. The text being embedded never becomes a tag.
/// </remarks>
public sealed class EmbeddingMetrics : IEmbeddingMetrics, IDisposable
{
    /// <summary>The meter name registered with the OpenTelemetry exporter.</summary>
    public const string MeterName = "MarqSpec.TradingCopilot.Ai";

    /// <summary>Embed calls, dimensioned by model and outcome — increments even for a failover, so a degrade is visible.</summary>
    public const string EmbedCalls = "ai.embed.calls";

    /// <summary>Input tokens billed, by model and outcome.</summary>
    public const string EmbedTokens = "ai.embed.tokens";

    /// <summary>Estimated USD cost, by model and outcome.</summary>
    public const string EmbedCost = "ai.embed.cost_usd";

    /// <summary>Embed call latency, by model and outcome.</summary>
    public const string EmbedLatency = "ai.embed.latency";

    private readonly Meter _meter;
    private readonly Counter<long> _calls;
    private readonly Counter<long> _tokens;
    private readonly Counter<double> _cost;
    private readonly Histogram<double> _latency;

    /// <summary>Creates the meter and its instruments.</summary>
    /// <param name="meterName">
    /// Overrides the meter name. Production leaves this null and gets <see cref="MeterName"/>; a test passes a
    /// unique name so its <c>MeterListener</c> observes only its own instance, never one from a class running in
    /// parallel (the same isolation the execution metrics need).
    /// </param>
    public EmbeddingMetrics(string? meterName = null)
    {
        _meter = new Meter(meterName ?? MeterName);

        _calls = _meter.CreateCounter<long>(
            EmbedCalls, unit: "{call}", description: "Embed calls by model and outcome — counts failovers too.");
        _tokens = _meter.CreateCounter<long>(
            EmbedTokens, unit: "{token}", description: "Input tokens billed, by model and outcome.");
        _cost = _meter.CreateCounter<double>(
            EmbedCost, unit: "{USD}", description: "Estimated dollar cost, by model and outcome.");
        _latency = _meter.CreateHistogram<double>(
            EmbedLatency, unit: "ms", description: "Embed call latency, by model and outcome.");
    }

    /// <inheritdoc />
    public void RecordEmbed(string model, EmbeddingOutcome outcome, int billedTokens, decimal estimatedCostUsd, TimeSpan latency)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("model", model),
            new("outcome", outcome.ToString().ToLowerInvariant()),
        ];

        _calls.Add(1, tags);
        _latency.Record(latency.TotalMilliseconds, tags);

        // Tokens and cost are recorded even at zero: a failover's zero is a real data point (this call cost
        // nothing because it delivered nothing), not an absence.
        _tokens.Add(billedTokens, tags);
        _cost.Add((double)estimatedCostUsd, tags);
    }

    /// <summary>Disposes the meter.</summary>
    public void Dispose() => _meter.Dispose();
}
