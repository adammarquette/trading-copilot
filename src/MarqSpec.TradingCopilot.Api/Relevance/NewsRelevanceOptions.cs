namespace MarqSpec.TradingCopilot.Api.Relevance;

/// <summary>
/// How often the news-relevance resolution pass runs (gh#359). Bound from the <c>Relevance</c> config section.
/// </summary>
/// <remarks>
/// Always on, like the indicator projection — its work is whatever news needs resolving (newly ingested, or stale
/// since a config change), so there is nothing to opt into; with no news the pass is a cheap no-op. The 60s default
/// resolves freshly ingested news promptly.
/// </remarks>
public sealed class NewsRelevanceOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Relevance";

    /// <summary>How often the resolution pass runs.</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>The pass cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
