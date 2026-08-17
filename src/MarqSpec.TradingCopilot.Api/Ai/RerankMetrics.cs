using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The AI-spend meter for Cohere <b>rerank</b> calls (gh#975, ADR-0002 / ADR-0008): call count, search units,
/// estimated cost and latency per call, dimensioned by model and outcome, on the <b>same</b>
/// <see cref="EmbeddingMetrics.MeterName"/> meter as embeddings and LLM calls — so Grafana shows true total AI spend,
/// rerank included, with no exporter change.
/// </summary>
/// <remarks>
/// Deliberately reuses <see cref="EmbeddingMetrics.MeterName"/> (the meter the OTel exporter already subscribes to),
/// so the rerank instruments export with no exporter change — they carry distinct names (<c>ai.rerank.*</c>)
/// alongside the embed (<c>ai.embed.*</c>) and LLM (<c>ai.llm.*</c>) ones. The dimension set is closed on purpose —
/// <c>model</c> and <c>outcome</c>, both low-cardinality — so the series count cannot explode; the ranked text is
/// never a tag. Mirrors <see cref="EmbeddingMetrics"/> instrument-for-instrument.
/// </remarks>
public sealed class RerankMetrics : IRerankMetrics, IDisposable
{
    /// <summary>Rerank calls, dimensioned by model and outcome — increments even for a degrade, so a fault is visible.</summary>
    public const string RerankCalls = "ai.rerank.calls";

    /// <summary>Search units billed, by model and outcome.</summary>
    public const string RerankSearches = "ai.rerank.searches";

    /// <summary>Estimated USD cost, by model and outcome.</summary>
    public const string RerankCost = "ai.rerank.cost_usd";

    /// <summary>Rerank call latency, by model and outcome.</summary>
    public const string RerankLatency = "ai.rerank.latency";

    private readonly Meter _meter;
    private readonly Counter<long> _calls;
    private readonly Counter<long> _searches;
    private readonly Counter<double> _cost;
    private readonly Histogram<double> _latency;

    /// <summary>Creates the meter and its instruments.</summary>
    /// <param name="meterName">
    /// Overrides the meter name. Production leaves this null and gets <see cref="EmbeddingMetrics.MeterName"/>; a test
    /// passes a unique name so its <c>MeterListener</c> observes only its own instance, never one from a class running
    /// in parallel (the same isolation the embedding + LLM metrics use).
    /// </param>
    public RerankMetrics(string? meterName = null)
    {
        _meter = new Meter(meterName ?? EmbeddingMetrics.MeterName);

        _calls = _meter.CreateCounter<long>(
            RerankCalls, unit: "{call}", description: "Rerank calls by model and outcome — counts degrades too.");
        _searches = _meter.CreateCounter<long>(
            RerankSearches, unit: "{search}", description: "Search units billed, by model and outcome.");
        _cost = _meter.CreateCounter<double>(
            RerankCost, unit: "{USD}", description: "Estimated dollar cost, by model and outcome.");
        _latency = _meter.CreateHistogram<double>(
            RerankLatency, unit: "ms", description: "Rerank call latency, by model and outcome.");
    }

    /// <inheritdoc />
    public void RecordRerank(string model, RerankOutcome outcome, int billedSearches, decimal estimatedCostUsd, TimeSpan latency)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("model", model),
            new("outcome", outcome.ToString().ToLowerInvariant()),
        ];

        _calls.Add(1, tags);
        _latency.Record(latency.TotalMilliseconds, tags);

        // Search units and cost are recorded even at zero: a degrade's zero is a real data point (this call cost
        // nothing because it delivered nothing), not an absence.
        _searches.Add(billedSearches, tags);
        _cost.Add((double)estimatedCostUsd, tags);
    }

    /// <summary>Disposes the meter.</summary>
    public void Dispose() => _meter.Dispose();
}
