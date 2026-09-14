namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>How a rerank call ended — the outcome dimension on every rerank-spend measurement (gh#975).</summary>
public enum RerankOutcome
{
    /// <summary>The provider returned a ranking.</summary>
    Reranked,

    /// <summary>The provider was rate-limited (HTTP 429) and the call fell back to passthrough (identity) order.</summary>
    RateLimited,

    /// <summary>The call failed (transport error, timeout, or an unexpected response) and fell back to passthrough order.</summary>
    Failed,
}

/// <summary>
/// Records the spend of every rerank call — search units, estimated cost, latency — so rerank usage is a first-class
/// tracked metric, not invisible spend on the operator's own key (engineering §AI-usage-and-spend, ADR-0008,
/// ADR-0002). Aggregated in Prometheus/Grafana by model and outcome, on the same <c>MarqSpec.TradingCopilot.Ai</c>
/// meter as embeddings and LLM calls.
/// </summary>
/// <remarks>
/// A seam so the provider can be unit-tested without a real meter, and so an unconfigured deployment gets a no-op via
/// <see cref="NullRerankMetrics"/>. It is a <b>required</b> dependency of the provider, never an optional one — an
/// optional metrics dependency silently defaults to no spend visibility in production, which is the failure this
/// exists to prevent.
/// </remarks>
public interface IRerankMetrics
{
    /// <summary>Records one rerank call, whatever its outcome.</summary>
    /// <param name="model">The rerank model, e.g. <c>rerank-english-v3.0</c>.</param>
    /// <param name="outcome">How the call ended.</param>
    /// <param name="billedSearches">Search units the provider billed; zero for a degraded call.</param>
    /// <param name="estimatedCostUsd">Estimated dollar cost of <paramref name="billedSearches"/>; zero when none billed.</param>
    /// <param name="latency">Wall-clock time the call took, success or failure.</param>
    void RecordRerank(string model, RerankOutcome outcome, int billedSearches, decimal estimatedCostUsd, TimeSpan latency);
}

/// <summary>The no-op metrics sink for a deployment with no rerank provider configured.</summary>
public sealed class NullRerankMetrics : IRerankMetrics
{
    /// <summary>The shared instance.</summary>
    public static NullRerankMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void RecordRerank(string model, RerankOutcome outcome, int billedSearches, decimal estimatedCostUsd, TimeSpan latency)
    {
    }
}
