using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The AI-spend meter for agent-review <b>LLM</b> calls (gh#477, ADR-0002 / ADR-0008): call count, tokens in/out,
/// estimated cost and latency per call, dimensioned by model, tier and outcome, on the <b>same</b>
/// <see cref="EmbeddingMetrics.MeterName"/> meter as embeddings — so Grafana shows true total AI spend, not just the
/// embedding half (gh#403).
/// </summary>
/// <remarks>
/// Deliberately reuses <see cref="EmbeddingMetrics.MeterName"/> (the meter the OTel exporter already subscribes to),
/// so the LLM instruments export with no exporter change — LLM and embed instruments share the meter but carry
/// distinct instrument names (<c>ai.llm.*</c> vs <c>ai.embed.*</c>). The dimension set is closed on purpose —
/// <c>feature</c>, <c>model</c>, <c>tier</c>, <c>outcome</c>, all low-cardinality — so the series count cannot explode; the reviewed
/// text is never a tag. Mirrors <see cref="EmbeddingMetrics"/> instrument-for-instrument.
/// </remarks>
public sealed class LlmMetrics : ILlmMetrics, IDisposable
{
    /// <summary>LLM calls, dimensioned by model, tier and outcome — increments for a failed / refused call too, so a fault is visible.</summary>
    public const string LlmCalls = "ai.llm.calls";

    /// <summary>Input (prompt) tokens billed, by model, tier and outcome.</summary>
    public const string LlmInputTokens = "ai.llm.input_tokens";

    /// <summary>Output (completion) tokens billed, by model, tier and outcome.</summary>
    public const string LlmOutputTokens = "ai.llm.output_tokens";

    /// <summary>Estimated USD cost, by model, tier and outcome.</summary>
    public const string LlmCost = "ai.llm.cost_usd";

    /// <summary>LLM call latency, by model, tier and outcome.</summary>
    public const string LlmLatency = "ai.llm.latency";

    private readonly Meter _meter;
    private readonly Counter<long> _calls;
    private readonly Counter<long> _inputTokens;
    private readonly Counter<long> _outputTokens;
    private readonly Counter<double> _cost;
    private readonly Histogram<double> _latency;

    /// <summary>Creates the meter and its instruments.</summary>
    /// <param name="meterName">
    /// Overrides the meter name. Production leaves this null and gets <see cref="EmbeddingMetrics.MeterName"/>; a test
    /// passes a unique name so its <c>MeterListener</c> observes only its own instance, never one from a class running
    /// in parallel (the same isolation the embedding + execution metrics use).
    /// </param>
    public LlmMetrics(string? meterName = null)
    {
        _meter = new Meter(meterName ?? EmbeddingMetrics.MeterName);

        _calls = _meter.CreateCounter<long>(
            LlmCalls, unit: "{call}", description: "Agent-review LLM calls by model, tier and outcome — counts failed calls too.");
        _inputTokens = _meter.CreateCounter<long>(
            LlmInputTokens, unit: "{token}", description: "Input (prompt) tokens billed, by model, tier and outcome.");
        _outputTokens = _meter.CreateCounter<long>(
            LlmOutputTokens, unit: "{token}", description: "Output (completion) tokens billed, by model, tier and outcome.");
        _cost = _meter.CreateCounter<double>(
            LlmCost, unit: "{USD}", description: "Estimated dollar cost, by feature, model, tier and outcome.");
        _latency = _meter.CreateHistogram<double>(
            LlmLatency, unit: "ms", description: "LLM call latency, by model, tier and outcome.");
    }

    /// <inheritdoc />
    public void RecordLlmCall(AiCallCost cost)
    {
        ArgumentNullException.ThrowIfNull(cost);

        KeyValuePair<string, object?>[] tags =
        [
            new("model", cost.Model),
            // Tier is always set for an LLM call (Triage / Deep); "none" is a defensive fallback that never fires here.
            new("tier", cost.Tier?.ToString().ToLowerInvariant() ?? "none"),
            new("outcome", cost.Outcome.ToString().ToLowerInvariant()),

            // gh#507: the ledger has recorded this since gh#431; without it here the metric half cannot answer

            // "what is costing money". Closed and low-cardinality like the rest -- the reviewed text is never a tag.

            new("feature", cost.Feature.ToString().ToLowerInvariant()),
        ];

        _calls.Add(1, tags);
        _latency.Record(cost.Latency.TotalMilliseconds, tags);

        // Tokens and cost are recorded even at zero: a failed call's zero is a real data point (billable latency that
        // produced nothing), not an absence — the exact failure regime a success-only meter would silently miss.
        _inputTokens.Add(cost.InputTokens, tags);
        _outputTokens.Add(cost.OutputTokens, tags);
        _cost.Add((double)cost.EstimatedCostUsd, tags);
    }

    /// <summary>Disposes the meter.</summary>
    public void Dispose() => _meter.Dispose();
}
