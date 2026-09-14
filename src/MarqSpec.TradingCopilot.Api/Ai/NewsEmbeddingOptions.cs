namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// How often the news-embedding pass runs (gh#377). Bound from the <c>Embedding</c> config section.
/// </summary>
/// <remarks>
/// Always on, like the relevance resolution pass it mirrors — its work is whatever news needs embedding (never
/// embedded, or revised since), so there is nothing to opt into; with no news, or no embedding provider configured,
/// the pass is a cheap no-op. The 60s default resolves freshly-ingested news promptly.
/// </remarks>
public sealed class NewsEmbeddingOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Embedding";

    /// <summary>How often the embedding pass runs.</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>The pass cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
