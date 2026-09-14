namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>How an embed call ended — the outcome dimension on every AI-spend measurement (gh#403).</summary>
public enum EmbeddingOutcome
{
    /// <summary>The provider returned a vector.</summary>
    Embedded,

    /// <summary>The provider was rate-limited (HTTP 429) and fell back to sparse.</summary>
    RateLimited,

    /// <summary>The call failed (transport error, timeout, or an unexpected response) and fell back to sparse.</summary>
    Failed,
}

/// <summary>
/// Records the spend of every embedding call — tokens, estimated cost, latency — so AI usage is a first-class
/// tracked metric, not invisible spend on the operator's own key (engineering §AI-usage-and-spend, ADR-0008,
/// ADR-0002). Aggregated in Prometheus/Grafana by model and outcome.
/// </summary>
/// <remarks>
/// A seam so the provider can be unit-tested without a real meter, and so an unconfigured deployment gets a
/// no-op via <see cref="NullEmbeddingMetrics"/>. It is a <b>required</b> dependency of the provider, never an
/// optional one — an optional metrics dependency silently defaults to no spend visibility in production, which
/// is the failure this exists to prevent.
/// </remarks>
public interface IEmbeddingMetrics
{
    /// <summary>Records one embed call, whatever its outcome.</summary>
    /// <param name="model">The embedding model, e.g. <c>embed-english-v3.0</c>.</param>
    /// <param name="outcome">How the call ended.</param>
    /// <param name="billedTokens">Tokens the provider billed; zero for a failed / rate-limited call.</param>
    /// <param name="estimatedCostUsd">Estimated dollar cost of <paramref name="billedTokens"/>; zero when none billed.</param>
    /// <param name="latency">Wall-clock time the call took, success or failure.</param>
    void RecordEmbed(string model, EmbeddingOutcome outcome, int billedTokens, decimal estimatedCostUsd, TimeSpan latency);
}

/// <summary>The no-op metrics sink for a deployment with no embedding provider configured.</summary>
public sealed class NullEmbeddingMetrics : IEmbeddingMetrics
{
    /// <summary>The shared instance.</summary>
    public static NullEmbeddingMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void RecordEmbed(string model, EmbeddingOutcome outcome, int billedTokens, decimal estimatedCostUsd, TimeSpan latency)
    {
    }
}
